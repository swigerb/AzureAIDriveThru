using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #102 review item 1: the two-pack persona_mismatch conformance row. Per the task
/// brief, this row is added by whichever of #101/#102 merges second, using the merged-in
/// persona support -- #101 (the brand-word-baseline ratchet + persona-conformance infra) has
/// already merged into dev by the time this revision was written, so this file adds it.
///
/// Mirrors session_manager.py's own resume() contract (see
/// app/backend/tests/test_persona_binding.py's ResumePersonaMismatchTests for the Python-side
/// unit-level equivalent): a session can only ever resume under the SAME persona it was
/// originally bound to. A resume attempt presenting a valid, still-live resume id but connecting
/// under a DIFFERENT persona than the one it was issued under must be rejected as
/// "persona_mismatch" -- never silently honoured under either persona, and never confused with
/// "unknown"/"expired"/"malformed" (which don't apply here: the id is genuinely valid, just for a
/// different persona).
///
/// Deliberately UNTAGGED, same reasoning as <see cref="PersonaDiscoveryConformanceTests"/>.
/// </summary>
[Collection(TwoPersonaConformanceCollection.Name)]
public sealed class PersonaMismatchConformanceTests(TwoPersonaConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task A_resume_id_issued_under_one_persona_is_rejected_as_persona_mismatch_under_another() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        // First connection: bound to test-alpha explicitly.
        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var first = await RealtimeBrowserClient.ConnectAsync(
            fixture.Backend!.BaseUri, persona: TwoPersonaConformanceFixture.PersonaA, cancellationToken: ct);
        Assert.True(await firstConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await first.SendStartSessionAsync(cancellationToken: ct);
        var firstMetadata = await first.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(firstMetadata is not null);
        Assert.Equal(TwoPersonaConformanceFixture.PersonaA, firstMetadata!.Json.GetProperty("persona").GetString());
        var resumeId = firstMetadata.Json.GetProperty("resumeId").GetString();
        Assert.False(string.IsNullOrEmpty(resumeId));

        // Drop without extension.end_session -- a genuine, resumable detach.
        await first.CloseAsync(cancellationToken: ct);
        await first.WaitForCloseAsync(FrameTimeout, ct);
        await first.DisposeAsync();

        // Second connection: bound to test-beta, presenting test-alpha's resume id as its first
        // frame. The id is valid (right length, actually issued, session still live) but for a
        // DIFFERENT persona than this socket's own -- must be persona_mismatch, not "unknown".
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(
            fixture.Backend!.BaseUri, persona: TwoPersonaConformanceFixture.PersonaB, cancellationToken: ct);
        Assert.True(await secondConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(resumeId!, ct);

        var rejected = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected", FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected extension.resume_rejected for a persona-mismatched resume id.");
        Assert.Equal("persona_mismatch", rejected!.Json.GetProperty("reason").GetString());

        // The socket continues normally under its OWN persona (test-beta) -- a fresh, re-announced
        // session, never the test-alpha one it tried (and failed) to resume.
        var freshMetadata = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata" && f.Sequence > rejected.Sequence, FrameTimeout, ct);
        Assert.True(freshMetadata is not null, "Expected a fresh re-announce after the persona_mismatch rejection.");
        Assert.Equal(TwoPersonaConformanceFixture.PersonaB, freshMetadata!.Json.GetProperty("persona").GetString());
        Assert.NotEqual(resumeId, freshMetadata.Json.GetProperty("resumeId").GetString());
    });
}
