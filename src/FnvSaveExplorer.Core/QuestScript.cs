namespace FnvSaveExplorer.Core;

/// <summary>The quest-affecting script verbs the Pip-Boy interpreter understands (ROADMAP §6 #16). FNV masters
/// keep quest stage result-scripts as <b>source text</b> (<c>SCTX</c>), so the interpreter reads these literally
/// rather than executing bytecode. Each names its <b>target quest by editor id</b> as its first argument.</summary>
public enum QuestScriptVerb
{
    /// <summary><c>SetObjectiveDisplayed &lt;quest&gt; &lt;obj&gt; [1|0]</c> — show/hide an objective in the Pip-Boy.</summary>
    SetObjectiveDisplayed,
    /// <summary><c>SetObjectiveCompleted &lt;quest&gt; &lt;obj&gt; [1|0]</c> — tick/untick an objective.</summary>
    SetObjectiveCompleted,
    /// <summary><c>SetStage &lt;quest&gt; &lt;stage&gt;</c> — advance a quest (also starts it if not running).</summary>
    SetStage,
    /// <summary><c>StartQuest &lt;quest&gt;</c> — begin running a quest.</summary>
    StartQuest,
    /// <summary><c>StopQuest &lt;quest&gt;</c> — stop a quest (removes it from the Pip-Boy).</summary>
    StopQuest,
    /// <summary><c>CompleteQuest &lt;quest&gt;</c> — mark a quest completed (greyed in the Pip-Boy).</summary>
    CompleteQuest,
    /// <summary><c>FailQuest &lt;quest&gt;</c> — mark a quest failed.</summary>
    FailQuest,
    /// <summary><c>CompleteAllObjectives &lt;quest&gt;</c> — tick every displayed objective of a quest.</summary>
    CompleteAllObjectives,
}

/// <summary>One quest-affecting call parsed from a stage's result-script text (ROADMAP §6 #16). <see cref="Arg1"/>
/// is the objective/stage index (where the verb takes one), <see cref="Arg2"/> the on/off flag for the
/// objective verbs (defaulting to 1 = on when the script omits it). <see cref="Conditional"/> is true when the
/// call sits inside an <c>if … endif</c> block — the interpreter still applies it (a Phase-A over-approximation)
/// but the flag marks that its firing isn't guaranteed without evaluating the condition.</summary>
public sealed record QuestScriptEffect(
    QuestScriptVerb Verb, string TargetQuestEdid, int Arg1, int Arg2, bool Conditional);

/// <summary>
/// Parses FNV quest stage result-script <b>source text</b> (<c>SCTX</c>) into the structured quest effects the
/// Pip-Boy interpreter applies (ROADMAP §6 #16). This is a deliberately small <i>literal</i> scan, not a script
/// engine: it recognises a fixed set of quest verbs (<see cref="QuestScriptVerb"/>) whose arguments are constants,
/// tracks <c>if/endif</c> nesting only to flag conditional calls, and ignores everything else (reference method
/// calls like <c>SomeRef.Enable</c>, variable assignments, comments). Calls whose arguments are variables rather
/// than integer literals are skipped — they can't be resolved without executing the script.
/// </summary>
public static class QuestScript
{
    private static readonly Dictionary<string, QuestScriptVerb> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["setobjectivedisplayed"] = QuestScriptVerb.SetObjectiveDisplayed,
        ["setobjectivecompleted"] = QuestScriptVerb.SetObjectiveCompleted,
        ["setstage"] = QuestScriptVerb.SetStage,
        ["startquest"] = QuestScriptVerb.StartQuest,
        ["stopquest"] = QuestScriptVerb.StopQuest,
        ["completequest"] = QuestScriptVerb.CompleteQuest,
        ["failquest"] = QuestScriptVerb.FailQuest,
        ["completeallobjectives"] = QuestScriptVerb.CompleteAllObjectives,
    };

    /// <summary>Whether a verb takes an objective/stage index as its second token (after the quest edid).</summary>
    private static bool HasIndexArg(QuestScriptVerb v) =>
        v is QuestScriptVerb.SetObjectiveDisplayed or QuestScriptVerb.SetObjectiveCompleted or QuestScriptVerb.SetStage;

    /// <summary>Whether a verb takes an on/off flag as its third token (the objective verbs).</summary>
    private static bool HasOnOffArg(QuestScriptVerb v) =>
        v is QuestScriptVerb.SetObjectiveDisplayed or QuestScriptVerb.SetObjectiveCompleted;

    /// <summary>Extracts the quest effects from a stage's result-script source text. Returns an empty list for
    /// null/blank text. Lines are comment-stripped; <c>if/elseif/else/endif</c> only adjust the conditional depth.</summary>
    public static IReadOnlyList<QuestScriptEffect> Parse(string? sctx)
    {
        if (string.IsNullOrWhiteSpace(sctx))
            return [];

        var effects = new List<QuestScriptEffect>();
        var depth = 0;
        foreach (var rawLine in sctx.Split('\n'))
        {
            // Strip a line comment (";" to end) and surrounding whitespace.
            var line = rawLine;
            var semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi];
            line = line.Trim();
            if (line.Length == 0) continue;

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var head = tokens[0];

            // Conditional nesting — affects only the Conditional flag, never which calls we record.
            if (head.Equals("if", StringComparison.OrdinalIgnoreCase)) { depth++; continue; }
            if (head.Equals("endif", StringComparison.OrdinalIgnoreCase)) { depth = Math.Max(0, depth - 1); continue; }
            if (head.Equals("elseif", StringComparison.OrdinalIgnoreCase) || head.Equals("else", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Verbs.TryGetValue(head, out var verb) || tokens.Length < 2)
                continue;

            var target = tokens[1];
            int arg1 = 0, arg2 = 1; // arg2 defaults to 1 (on) when the objective verb omits the flag
            if (HasIndexArg(verb))
            {
                if (tokens.Length < 3 || !int.TryParse(tokens[2], out arg1))
                    continue; // non-literal index (a variable) — can't resolve statically
            }
            if (HasOnOffArg(verb) && tokens.Length >= 4 && int.TryParse(tokens[3], out var on))
                arg2 = on;

            effects.Add(new QuestScriptEffect(verb, target, arg1, arg2, depth > 0));
        }
        return effects;
    }
}
