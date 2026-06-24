using System.Text.RegularExpressions;

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

/// <summary>One quest-affecting call parsed from a stage's (or GameMode block's) result-script text (ROADMAP
/// §6 #16). <see cref="Arg1"/> is the objective/stage index (where the verb takes one), <see cref="Arg2"/> the
/// on/off flag for the objective verbs (defaulting to 1 = on when the script omits it). <see cref="Guards"/>
/// holds the source text of every enclosing <c>if</c>/<c>elseif</c> condition (outermost first); the call fires
/// only when all of them hold (see <see cref="ScriptStartup"/> for the startup evaluation).</summary>
public sealed record QuestScriptEffect(
    QuestScriptVerb Verb, string TargetQuestEdid, int Arg1, int Arg2, IReadOnlyList<string> Guards)
{
    /// <summary>The call sits inside at least one <c>if</c> block.</summary>
    public bool Conditional => Guards.Count > 0;
}

/// <summary>
/// Parses FNV quest stage result-script <b>source text</b> (<c>SCTX</c>) into the structured quest effects the
/// Pip-Boy interpreter applies (ROADMAP §6 #16). This is a deliberately small <i>literal</i> scan, not a script
/// engine: it recognises a fixed set of quest verbs (<see cref="QuestScriptVerb"/>) whose arguments are constants,
/// records the enclosing <c>if</c>/<c>elseif</c> guard text, and ignores everything else (reference method calls
/// like <c>SomeRef.Enable</c>, comments). Calls whose arguments are variables rather than integer literals are
/// skipped — they can't be resolved without executing the script.
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

    private static bool HasIndexArg(QuestScriptVerb v) =>
        v is QuestScriptVerb.SetObjectiveDisplayed or QuestScriptVerb.SetObjectiveCompleted or QuestScriptVerb.SetStage;

    private static bool HasOnOffArg(QuestScriptVerb v) =>
        v is QuestScriptVerb.SetObjectiveDisplayed or QuestScriptVerb.SetObjectiveCompleted;

    /// <summary>Comment-strips a script line and returns its whitespace-split tokens (empty when blank).</summary>
    private static string[] Tokens(string rawLine, out string trimmed)
    {
        var line = rawLine;
        var semi = line.IndexOf(';');
        if (semi >= 0) line = line[..semi];
        trimmed = line.Trim();
        return trimmed.Length == 0 ? [] : trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>True when <paramref name="trimmed"/> opens with control keyword <paramref name="kw"/> — i.e. the
    /// keyword is followed by whitespace, an opening paren, or end-of-line. This tolerates the no-space form
    /// (<c>elseif(nEvent == 3)</c>, <c>if(x)</c>) that FNV scripts use, which a plain token-equality check misses
    /// (it would tokenise <c>elseif(nEvent</c> as one word, desyncing the guard stack — ROADMAP §6 #16).</summary>
    private static bool OpensWith(string trimmed, string kw) =>
        trimmed.StartsWith(kw, StringComparison.OrdinalIgnoreCase) &&
        (trimmed.Length == kw.Length || trimmed[kw.Length] == '(' || char.IsWhiteSpace(trimmed[kw.Length]));

    /// <summary>Maintains the enclosing if/elseif/else guard stack for one script line; returns true when the line
    /// was a control-flow keyword (and should not be processed further). Order matters: <c>elseif</c> is tested
    /// before <c>else</c>, and <c>endif</c> before <c>if</c>, so the shorter keyword can't swallow the longer.</summary>
    private static bool TrackGuards(string[] tokens, string trimmed, List<string> guards)
    {
        if (OpensWith(trimmed, "elseif")) { if (guards.Count > 0) guards[^1] = trimmed[6..].Trim(); return true; }
        if (OpensWith(trimmed, "else")) { if (guards.Count > 0) guards[^1] = "0"; return true; }
        if (OpensWith(trimmed, "endif")) { if (guards.Count > 0) guards.RemoveAt(guards.Count - 1); return true; }
        if (OpensWith(trimmed, "if")) { guards.Add(trimmed[2..].Trim()); return true; }
        return false;
    }

    /// <summary>Extracts the quest effects from a stage's (or GameMode block's) result-script source text. Returns
    /// an empty list for null/blank text. Each effect records its enclosing <c>if</c>/<c>elseif</c> guards.</summary>
    public static IReadOnlyList<QuestScriptEffect> Parse(string? sctx)
    {
        if (string.IsNullOrWhiteSpace(sctx))
            return [];

        var effects = new List<QuestScriptEffect>();
        var guards = new List<string>();
        foreach (var rawLine in sctx.Split('\n'))
        {
            var tokens = Tokens(rawLine, out var trimmed);
            if (tokens.Length == 0 || TrackGuards(tokens, trimmed, guards))
                continue;

            if (!Verbs.TryGetValue(tokens[0], out var verb) || tokens.Length < 2)
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

            effects.Add(new QuestScriptEffect(verb, target, arg1, arg2, [.. guards]));
        }
        return effects;
    }

    /// <summary>Extracts <c>set &lt;var&gt; to &lt;value&gt;</c> assignments (with their enclosing guards) from
    /// script text — the do-once flags and timers a GameMode block drives. <c>Value</c> is the constant assigned,
    /// or null when the right-hand side is an expression/function (a counter or timer). <see cref="ScriptStartup"/>
    /// runs these to a fixpoint to learn each local variable's reachable startup values (ROADMAP §6 #16).</summary>
    public static IReadOnlyList<(string Var, int? Value, IReadOnlyList<string> Guards)> ParseAssignments(string? sctx)
    {
        if (string.IsNullOrWhiteSpace(sctx))
            return [];

        var sets = new List<(string, int?, IReadOnlyList<string>)>();
        var guards = new List<string>();
        foreach (var rawLine in sctx.Split('\n'))
        {
            var tokens = Tokens(rawLine, out var trimmed);
            if (tokens.Length == 0 || TrackGuards(tokens, trimmed, guards))
                continue;

            // set <var> to <value>   ("set"/"to" case-insensitive). A 4-token "set x to <int>" is a constant
            // assignment; anything longer (an expression / function) leaves Value null (the variable is dynamic).
            if (tokens.Length >= 4 && tokens[0].Equals("set", StringComparison.OrdinalIgnoreCase) &&
                tokens[2].Equals("to", StringComparison.OrdinalIgnoreCase))
            {
                int? value = tokens.Length == 4 && int.TryParse(tokens[3], out var v) ? v : null;
                sets.Add((tokens[1], value, [.. guards]));
            }
        }
        return sets;
    }

    /// <summary>Extracts counter <b>increments</b> from script text (ROADMAP §6 #16 Stage 1) — assignments of the
    /// form <c>set &lt;q&gt;.&lt;v&gt; to &lt;q&gt;.&lt;v&gt; ± N</c> (qualified: <c>QuestEdid</c> names the quest
    /// the counter belongs to — the actor <c>OnDeath</c>-style form
    /// <c>set VMS16.nGangerDeathCount to (VMS16.nGangerDeathCount + 1)</c>) and the bare
    /// <c>set &lt;v&gt; to &lt;v&gt; ± N</c> (<c>QuestEdid</c> = <c>""</c> — the script's own quest). Only TRUE
    /// increments are returned: the right-hand side must reference the same variable, so a plain reset
    /// (<c>set v to 0</c>) is excluded. Parenthesised RHS is tolerated. <c>Delta</c> is the signed step (+1, -1, …).
    /// These are the kill/collect counters a quest's GameMode then gates completion on (<see cref="FindCounterComparison"/>).</summary>
    public static IReadOnlyList<(string QuestEdid, string Counter, int Delta)> ParseCounterIncrements(string? sctx)
    {
        if (string.IsNullOrWhiteSpace(sctx))
            return [];

        var result = new List<(string, string, int)>();
        foreach (var rawLine in sctx.Split('\n'))
        {
            var tokens = Tokens(rawLine, out _);
            // set <lhs> to <rhs...>   (a 4+-token assignment; "set"/"to" case-insensitive)
            if (tokens.Length < 4 || !tokens[0].Equals("set", StringComparison.OrdinalIgnoreCase) ||
                !tokens[2].Equals("to", StringComparison.OrdinalIgnoreCase))
                continue;

            var lhs = tokens[1];
            string edid = "", counter = lhs;
            var dot = lhs.IndexOf('.');
            if (dot > 0) { edid = lhs[..dot]; counter = lhs[(dot + 1)..]; }
            if (counter.Length == 0)
                continue;

            // RHS: the remaining tokens with parentheses split out, so "(VMS16.nGangerDeathCount + 1)" tokenises to
            // the variable, the operator, and the literal.
            var rhs = string.Join(' ', tokens[3..]).Replace("(", " ").Replace(")", " ");
            if (TryIncrementDelta(rhs, edid, counter, out var delta))
                result.Add((edid, counter, delta));
        }
        return result;
    }

    /// <summary>True when <paramref name="rhs"/> is the same variable plus/minus an integer literal (a true
    /// increment), yielding the signed <paramref name="delta"/>. Requires the variable to appear on the RHS so a
    /// reset (<c>set v to 0</c>) is rejected.</summary>
    private static bool TryIncrementDelta(string rhs, string edid, string counter, out int delta)
    {
        delta = 0;
        var qualified = edid.Length > 0 ? edid + "." + counter : counter;
        var toks = rhs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!toks.Any(t => t.Equals(qualified, StringComparison.OrdinalIgnoreCase) ||
                           t.Equals(counter, StringComparison.OrdinalIgnoreCase)))
            return false; // RHS doesn't reference the counter — a reset/assignment, not an increment
        for (var i = 0; i + 1 < toks.Length; i++)
            if ((toks[i] == "+" || toks[i] == "-") && int.TryParse(toks[i + 1], out var n))
            {
                delta = toks[i] == "-" ? -n : n;
                return true;
            }
        return false;
    }

    // A `<var> <op> <int>` comparison (e.g. `nGangerDeathCount >= 6`); the identifier allows dots for a qualified
    // `Quest.counter` form. The reversed `<int> <op> <var>` is matched separately (with the operator flipped).
    private static readonly Regex CounterCmp =
        new(@"([A-Za-z_][A-Za-z0-9_.]*)\s*(==|!=|>=|<=|>|<)\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex CounterCmpReversed =
        new(@"(\d+)\s*(==|!=|>=|<=|>|<)\s*([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled);

    /// <summary>Finds, in a guard's source text, a comparison of one of <paramref name="counters"/> against an
    /// integer literal — <c>&lt;counter&gt; &lt;op&gt; &lt;N&gt;</c> (or the reversed <c>&lt;N&gt; &lt;op&gt;
    /// &lt;counter&gt;</c>, operator flipped) — returning the matched counter name (its post-dot suffix for a
    /// qualified form), comparison operator, and threshold. This is how a quest's GameMode counter-gate
    /// (<c>nGangerDeathCount >= 6</c>) is detected (ROADMAP §6 #16 Stage 1). Returns null when the guard tests no
    /// listed counter.</summary>
    public static (string Counter, string Op, int Threshold)? FindCounterComparison(
        string? guard, IReadOnlyCollection<string> counters)
    {
        if (string.IsNullOrWhiteSpace(guard) || counters.Count == 0)
            return null;
        var set = counters as ISet<string> ?? new HashSet<string>(counters, StringComparer.OrdinalIgnoreCase);

        static string Suffix(string id) { var d = id.LastIndexOf('.'); return d >= 0 ? id[(d + 1)..] : id; }

        foreach (Match m in CounterCmp.Matches(guard))
        {
            var name = Suffix(m.Groups[1].Value);
            if (set.Contains(name) && int.TryParse(m.Groups[3].Value, out var n))
                return (name, m.Groups[2].Value, n);
        }
        foreach (Match m in CounterCmpReversed.Matches(guard))
        {
            var name = Suffix(m.Groups[3].Value);
            if (set.Contains(name) && int.TryParse(m.Groups[1].Value, out var n))
                return (name, Flip(m.Groups[2].Value), n);
        }
        return null;
    }

    private static string Flip(string op) => op switch { ">" => "<", "<" => ">", ">=" => "<=", "<=" => ">=", _ => op };
}

/// <summary>
/// Decides whether a Start-Game-Enabled quest's GameMode <c>if</c>-guards are satisfied <b>at game start</b>
/// (ROADMAP §6 #16) — i.e. whether the guarded <c>SetStage</c>/<c>StartQuest</c> fires at load with no player
/// action. It models the quest script's <b>local variables</b> (do-once flags, timers) as the set of values each
/// can reach from the GameMode block's own assignments, and treats all <b>world state</b> (query functions like
/// <c>GetStage</c>/<c>GetReputationThreshold</c>, <c>Ref.Method</c> calls, and globals) as default/unknown.
/// <para>A guard <b>holds at startup</b> when it is <i>satisfiable</i> under those reachable local values with
/// every world atom false: the intro radio quests' guard <c>DoOnceMessage == 1 &amp;&amp; StartTimer &lt;= 0</c>
/// holds (the do-once flag reaches 1, the timer counts down), while <c>iHouseObjective == 1</c> does not (that
/// local is only incremented under world-state guards, so it stays 0), and <c>GetReputationThreshold … &gt;= 2</c>
/// does not (a world atom). This is the precision lever that separates the four DLC-intro quests actually shown on
/// Save 57 from Start-Game-Enabled quests whose objectives only display once the player has made progress.</para>
/// Unparseable guards are treated as not holding (conservative — the caller won't fire).
/// </summary>
public sealed class ScriptStartup
{
    private const int Top = int.MinValue;              // sentinel "dynamic — any value reachable"
    private readonly Dictionary<string, HashSet<int>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _locals;

    private ScriptStartup(HashSet<string> locals) => _locals = locals;

    /// <summary>Builds the startup model for a GameMode block: runs its <c>set</c> assignments to a fixpoint,
    /// applying each only while its own guards are satisfiable, to learn every local's reachable startup values.</summary>
    public static ScriptStartup Analyze(string? gameModeScript, IReadOnlyCollection<string>? localVars)
    {
        var s = new ScriptStartup(new HashSet<string>(localVars ?? [], StringComparer.OrdinalIgnoreCase));
        var sets = QuestScript.ParseAssignments(gameModeScript);
        for (var pass = 0; pass < 8 && sets.Count > 0; pass++) // converges fast; bound guards against oscillation
        {
            var changed = false;
            foreach (var (var, value, guards) in sets)
            {
                if (!s._locals.Contains(var) || !guards.All(s.GuardHolds))
                    continue;
                var set = s._values.TryGetValue(var, out var existing) ? existing : s._values[var] = [0];
                if (set.Contains(Top))
                    continue;
                changed |= value is { } c ? set.Add(c) : set.Add(Top); // null RHS (expr/timer) => dynamic
            }
            if (!changed)
                break;
        }
        return s;
    }

    /// <summary>The values a local variable can reach at startup: a known set, <c>{0}</c> by default, or a set
    /// containing the <see cref="Top"/> sentinel when the variable is dynamic (a timer / counter).</summary>
    private HashSet<int> Reach(string name) => _values.TryGetValue(name, out var s) ? s : [0];

    /// <summary>True when <paramref name="guard"/> is satisfiable at game start under the reachable local values
    /// (world atoms false). Null/blank = no guard = holds. Unparseable = false.</summary>
    public bool GuardHolds(string? guard)
    {
        if (string.IsNullOrWhiteSpace(guard))
            return true;
        try
        {
            var tokens = Tokenize(guard);
            var pos = 0;
            var r = ParseOr(tokens, ref pos);
            return pos == tokens.Count && r;
        }
        catch
        {
            return false;
        }
    }

    // ----- a tiny tokenizer + recursive-descent satisfiability evaluator over local value-sets -----

    private static readonly string[] MultiCharOps = ["==", "!=", "<=", ">=", "&&", "||"];
    private static readonly string[] CompareOps = ["==", "!=", "<", "<=", ">", ">="];

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '(' or ')') { tokens.Add(c.ToString()); i++; continue; }
            var two = i + 1 < s.Length ? s.Substring(i, 2) : null;
            if (two is not null && Array.IndexOf(MultiCharOps, two) >= 0) { tokens.Add(two); i += 2; continue; }
            if (c is '<' or '>') { tokens.Add(c.ToString()); i++; continue; }
            var start = i;
            while (i < s.Length)
            {
                var d = s[i];
                if (char.IsWhiteSpace(d) || d is '(' or ')' or '<' or '>') break;
                if (i + 1 < s.Length && Array.IndexOf(MultiCharOps, s.Substring(i, 2)) >= 0) break;
                i++;
            }
            tokens.Add(s[start..i]);
        }
        return tokens;
    }

    private bool ParseOr(List<string> t, ref int pos)
    {
        var v = ParseAnd(t, ref pos);
        while (pos < t.Count && t[pos] == "||") { pos++; v = ParseAnd(t, ref pos) | v; }
        return v;
    }

    private bool ParseAnd(List<string> t, ref int pos)
    {
        var v = ParseCmp(t, ref pos);
        while (pos < t.Count && t[pos] == "&&") { pos++; v = ParseCmp(t, ref pos) & v; }
        return v;
    }

    // cmp := '(' or-expr ')' | operand ( compareOp operand )?   (bare operand = truthiness)
    private bool ParseCmp(List<string> t, ref int pos)
    {
        if (pos < t.Count && t[pos] == "(")
        {
            pos++;
            var v = ParseOr(t, ref pos);
            if (pos < t.Count && t[pos] == ")") pos++;
            return v;
        }
        var left = ReadOperand(t, ref pos);
        if (pos < t.Count && Array.IndexOf(CompareOps, t[pos]) >= 0)
        {
            var op = t[pos++];
            var right = ReadOperand(t, ref pos);
            return SatCompare(left, op, right);
        }
        return Satisfies(left, v => v != 0); // bare truthiness
    }

    /// <summary>An operand's reachable integer values at startup: a numeric literal, a local variable's reachable
    /// set, or — for world state (a query function, a <c>Ref.Method</c> call, or a non-local global) — its
    /// game-start default of <c>{0}</c>. So <c>GetDisabled == 0</c> (a default-enabled ref) holds, while
    /// <c>GetReputationThreshold … &gt;= 2</c> / <c>GetStage … &gt;= 80</c> / a story-event <c>… == 1</c> do not.</summary>
    private HashSet<int> ReadOperand(List<string> t, ref int pos)
    {
        var words = new List<string>();
        while (pos < t.Count && t[pos] is not ("&&" or "||" or "(" or ")") && Array.IndexOf(CompareOps, t[pos]) < 0)
            words.Add(t[pos++]);
        if (words.Count == 1 && int.TryParse(words[0], out var n))
            return [n];                                    // numeric literal
        if (words.Count == 1 && !words[0].Contains('.') && _locals.Contains(words[0]))
            return Reach(words[0]);                         // a local variable's reachable set
        return [0];                                        // world state (function / member / global) — default 0
    }

    private static bool SatCompare(HashSet<int> left, string op, HashSet<int> right)
    {
        foreach (var a in left)
            foreach (var b in right)
                if (a == Top || b == Top || Compare(a, op, b))
                    return true;
        return false;
    }

    private static bool Satisfies(HashSet<int> set, Func<int, bool> pred) => set.Contains(Top) || set.Any(pred);

    private static bool Compare(int a, string op, int b) => op switch
    {
        "==" => a == b, "!=" => a != b, "<" => a < b, "<=" => a <= b, ">" => a > b, ">=" => a >= b, _ => false,
    };
}
