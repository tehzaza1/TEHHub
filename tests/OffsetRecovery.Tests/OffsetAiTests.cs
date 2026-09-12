using System.Text.Json;
using TEHhub.Ui;
using TEHhub.Utils;

internal static class OffsetAiTests
{
    internal static void Run(Action<bool, string> check)
    {
        const long moduleBase = 0x140000000;
        const long player = 0x76543210ABC0;
        var observed = new PrimaryRootSearchResult
        {
            Complete = true,
            Candidates = [new(moduleBase + 0x1230, 0x765432101000, 0xA0, 4,
                0x765432102000, 0x765432103000, player, 0x765432104000, 321)],
            StructuralRoots = [new(moduleBase + 0x5670, 0x765432105000, 0xB0, 7,
                [0x765432106000, 0x765432107000])],
            Rejections = new() { ["missing player graph"] = 2 },
        };
        // Ground truth belongs to the arena evaluator, never the model's observation.
        var arena = (GroundTruth: new PrimaryRootCandidate(moduleBase + 0xDEAD0,
            0x654321001000, 0xCAFE, 9, 0x654321002000, 0x654321003000,
            0x654321004000, 0x654321005000, 987654321), Observed: observed);
        var report = new PrimaryRootResearchReport
        {
            ModuleBase = moduleBase, GameSha256 = "test-image-hash",
            ModulePath = "private-player-path-not-for-model", ProcessId = 98765,
            Observations = [arena.Observed],
        };
        var evidence = OffsetAiResearch.BuildEvidence(report, out var ids);
        check(ids.SetEquals(["o0-c0", "o0-h0"]), "AI evidence scopes validated and structural IDs separately");
        check(evidence.Contains("slotRva=0x1230") && evidence.Contains("UNCONFIRMED structural hypothesis"),
            "AI evidence distinguishes module-relative slots and unconfirmed hypotheses");
        check(!evidence.Contains(player.ToString()) && !evidence.Contains(player.ToString("X")) &&
            !evidence.Contains("76543210") && !evidence.Contains(moduleBase.ToString("X")),
            "AI evidence omits native player, manager, UI and module pointers");
        check(!evidence.Contains(report.ModulePath) && !evidence.Contains("98765"),
            "AI evidence omits private executable path and process identity");
        check(!evidence.Contains("DEAD0") && !evidence.Contains("CAFE") && !evidence.Contains("987654321"),
            "AI evidence includes observed graph only, never arena ground truth");

        string Decision(string kind = "inspect", string candidate = "o0-c0", string probe = "player-components", string reason = "Check descendant ownership.") =>
            JsonSerializer.Serialize(new { decision = kind, candidateId = candidate, nextProbe = probe, reason });
        check(OffsetAiResearch.ValidateDecision(Decision(), ids).CandidateId == "o0-c0",
            "AI accepts inspection of an observed validated candidate");
        check(OffsetAiResearch.ValidateDecision(Decision(candidate: "o0-h0", probe: "ui-backlinks"), ids).CandidateId == "o0-h0",
            "AI accepts inspection of an observed structural hypothesis without confirming it");
        check(OffsetAiResearch.ValidateDecision(Decision("abstain", "", "rescan-in-world"), ids).Decision == "abstain",
            "AI accepts explicit abstention with no selected candidate");

        void Reject(string json, string message)
        {
            var rejected = false;
            try { OffsetAiResearch.ValidateDecision(json, ids); }
            catch (Exception) { rejected = true; }
            check(rejected, message);
        }
        Reject(Decision(candidate: "o99-c0"), "AI rejects invented candidate ID");
        Reject(Decision(candidate: "0x76543210ABC0"), "AI rejects native address as candidate ID");
        Reject(Decision(probe: "install-offset"), "AI rejects unsupported probe or installation request");
        Reject(Decision("confirmed"), "AI rejects unsupported confirmation decision");
        Reject(Decision()[..^1] + ",\"offset\":123}", "AI rejects unknown JSON properties");
        Reject(new string(' ', 16001), "AI rejects oversized model response before parsing");
        Reject(Decision("abstain", "o0-c0"), "AI rejects abstention that selects a candidate");
        Reject(Decision(reason: " "), "AI rejects blank explanation");
        Reject(Decision(reason: new string('x', 2001)), "AI rejects oversized explanation");
        Reject("null", "AI rejects null decision object");
        Reject("{}", "AI rejects missing decision fields");
        foreach (var field in new[] { "decision", "candidateId", "nextProbe", "reason" })
        {
            var fields = new Dictionary<string, object?>
            {
                ["decision"] = "abstain", ["candidateId"] = "",
                ["nextProbe"] = "rescan-in-world", ["reason"] = "No independent proof.",
            };
            fields[field] = null;
            Reject(JsonSerializer.Serialize(fields), $"AI rejects null {field}");
        }

        for (var i = 1; i < 40; i++)
        {
            observed.Candidates.Add(observed.Candidates[0] with { GlobalSlot = moduleBase + 0x1230 + 8 * i });
            observed.StructuralRoots.Add(observed.StructuralRoots[0] with { GlobalSlot = moduleBase + 0x5670 + 8 * i });
        }
        evidence = OffsetAiResearch.BuildEvidence(report, out ids);
        check(ids.Count == 64 && ids.Contains("o0-c31") && ids.Contains("o0-h31") &&
            !ids.Contains("o0-c32") && !ids.Contains("o0-h32"),
            "AI evidence caps each candidate category at 32 and excludes truncated IDs");
        Reject(Decision(candidate: "o0-c32"), "AI cannot select a candidate removed by input truncation");
        var emptyEvidence = OffsetAiResearch.BuildEvidence(new(), out var emptyIds);
        check(emptyIds.Count == 0 && !emptyEvidence.Contains("o0-c0"), "AI empty observations invent no candidate IDs");
    }
}
