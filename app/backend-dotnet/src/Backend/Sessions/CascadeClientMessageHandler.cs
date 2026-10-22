using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Realtime;

namespace Backend.Sessions;

/// <summary>Dispatches client messages into one cascade connection's turn state.</summary>
internal sealed class CascadeClientMessageHandler(CascadeSessionContext context)
{
    public Task HandleAsync(JsonObject data) =>
        CascadeProcessor.GetString(data, "type") switch
        {
            "input_audio_buffer.append" => HandleAudioAppendAsync(data),
            "input_audio_buffer.clear" => ClearAudioBufferAsync(),
            "extension.set_voice" => SetVoiceAsync(data),
            "extension.demo_rewards" => HandleRewardsAsync(data),
            "extension.resume" => RejectLateResumeAsync(),
            _ => Task.CompletedTask,
        };

    private Task HandleRewardsAsync(JsonObject data)
    {
        context.SessionManager?.TouchActivity(context.SessionId);
        return context.NotifyClientAsync(RewardsDemoFrames.Handle(context.ToolExecutor, data), context.CancellationToken);
    }

    private async Task HandleAudioAppendAsync(JsonObject data)
    {
        var audioB64 = CascadeProcessor.GetString(data, "audio");
        if (string.IsNullOrEmpty(audioB64))
        {
            return;
        }
        byte[] pcm;
        try
        {
            pcm = Convert.FromBase64String(audioB64);
        }
        catch (FormatException)
        {
            return;
        }
        var state = context.State;
        if (state.NudgeEligible && !state.NudgeArmed)
        {
            state.NudgeArmed = true;
            context.TurnCoordinator.ScheduleNudge();
        }
        var vadEvent = context.Detector.Feed(pcm, context.NowSeconds());
        if (vadEvent == "speech_started")
        {
            context.TurnCoordinator.CancelNudge("guest started speaking");
            context.TurnCoordinator.BargeIn();
        }
        else if (vadEvent == "speech_stopped")
        {
            context.TurnCoordinator.CancelNudge("guest turn started");
            var fragment = context.Detector.TakeBuffer();
            context.Detector.Reset();
            byte[] turnAudio;
            int dropped;
            lock (context.TurnRegistryLock)
            {
                var carried = context.State.UntranscribedAudio;
                dropped = carried.Length == 0 ? 0 : Math.Max(0, carried.Length + fragment.Length - MaxCarriedTurnAudioBytes);
                turnAudio = CarryForward(carried, fragment);
                context.State.UntranscribedAudio = turnAudio;
            }
            if (dropped > 0)
            {
                context.Logger.CascadeCarriedAudioTruncated(MaxCarriedTurnAudioBytes, dropped, context.SessionId);
            }
            ScheduleTurn(turnAudio);
        }
    }

    /// <summary>Upper bound on audio carried across barge-in-cancelled turns: 30 s of PCM16 at
    /// 24 kHz. Applies only when carried audio is prepended; a single utterance is never capped.</summary>
    internal const int MaxCarriedTurnAudioBytes = 30 * CascadeProcessor.AudioSampleRate * 2;

    internal static byte[] CarryForward(byte[] carried, byte[] fragment)
    {
        if (carried.Length == 0)
        {
            return fragment;
        }
        var combined = new byte[carried.Length + fragment.Length];
        carried.CopyTo(combined, 0);
        fragment.CopyTo(combined, carried.Length);
        return combined.Length <= MaxCarriedTurnAudioBytes
            ? combined
            : combined[^MaxCarriedTurnAudioBytes..];
    }

    private void ScheduleTurn(byte[] turnAudio)
    {
        var bargeInTail = context.State.BargeInTail;
        var (cts, task) = context.TurnCoordinator.Spawn(async turnCt =>
        {
            if (bargeInTail is not null)
            {
                await bargeInTail.WaitAsync(turnCt).ConfigureAwait(false);
            }
            await context.ProcessTurnAsync(turnAudio, turnCt).ConfigureAwait(false);
        }, "turn");
        context.State.CurrentTurnCts = cts;
        context.State.CurrentTurnTask = task;
    }

    private Task ClearAudioBufferAsync()
    {
        context.Detector.Reset();
        lock (context.TurnRegistryLock)
        {
            context.State.UntranscribedAudio = [];
        }
        return Task.CompletedTask;
    }

    private Task SetVoiceAsync(JsonObject data)
    {
        var candidate = CascadeProcessor.GetString(data, "voice");
        var voice = ClientServerFilter.SanitizeVoice(candidate, context.AllowedVoices);
        if (voice is null)
        {
            context.Logger.CascadeSetVoiceDropped(candidate, context.SessionId);
        }
        else
        {
            context.State.Voice = voice;
        }
        return Task.CompletedTask;
    }

    private Task RejectLateResumeAsync() =>
        CascadeProcessor.SendTextAsync(context.BrowserSocket, new JsonObject
        {
            ["type"] = "extension.resume_rejected",
            ["reason"] = "not_first_frame",
        }.ToJsonString(), context.CancellationToken, context.CancellationToken);
}
