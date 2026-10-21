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
            "extension.resume" => RejectLateResumeAsync(),
            _ => Task.CompletedTask,
        };

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
            var turnAudio = context.Detector.TakeBuffer();
            context.Detector.Reset();
            ScheduleTurn(turnAudio);
        }
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
