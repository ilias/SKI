// SKI(BCWY) Combinator Calculus Interpreter
// Grammar:
//   line = Name '=' expr      (definition)
//        | expr               (reduction)
//        | let x = e1 in e2  (local binding, desugars to (\x. e2) e1)
//   expr = ATOM | '(' expr expr ')'
//   ATOM = S | K | I | B | C | W | Y | <user-defined Name>
//
// Reduction rules:
//   I x       →  x
//   K x y     →  x
//   S x y z   →  (x z)(y z)
//   B x y z   →  x (y z)          (composition)
//   C x y z   →  x z y            (flip)
//   W x y     →  x y y            (duplication)
//   Y f (lazy)→  f (Y f) applied to next argument
//
// Lambda abstraction: \x y. body  — compiled to SKI via bracket abstraction
// Integer literals:   0, 1, 42    — converted to Church numerals at parse time
// Parameterized defs: F x y = body — sugar for  F = \x y. body
// Let-expressions:    let x = e1 in e2  — sugar for  (\x. e2) e1
// Letrec-exprs:        letrec f x = body in e2  — recursive local binding (Y-based)
// Import directive:   import <path>  — inside .ski files, loads another file
// Run with: dotnet run -- "(((S K) K) I)"
// Or interactively with no arguments.

using SKI;
using SysReadLine = System.ReadLine;

var env = new Dictionary<string, Expr>(StringComparer.Ordinal);
bool traceMode = false;
int maxSteps = 1_000_000;

// Auto-load init.ski from the same directory as the executable (or cwd).
var initPath = FindInitFile();
if (initPath is not null)
    LoadFile(initPath, env, silent: true, maxSteps: maxSteps);

if (args.Length > 0)
{
    RunLine(string.Concat(args), env, traceMode, maxSteps);
}
else
{
    // Configure ReadLine for REPL history and tab completion
    SysReadLine.HistoryEnabled = true;
    SysReadLine.AutoCompletionHandler = new SkiAutoComplete(() => env);

    PrintHelp(initPath);
    while (true)
    {
        string? rawLine;
        try { rawLine = SysReadLine.Read("> "); }
        catch (Exception) { rawLine = Console.ReadLine(); }
        if (rawLine is null || rawLine.Equals("exit", StringComparison.OrdinalIgnoreCase))
            break;
        if (string.IsNullOrWhiteSpace(rawLine))
            continue;

        // Multi-line continuation: a line ending with '\' is joined with the next.
        var sb = new System.Text.StringBuilder(rawLine.TrimEnd());
        while (sb.Length > 0 && sb[^1] == '\\')
        {
            sb.Remove(sb.Length - 1, 1);
            Console.Write("  ");
            var cont = Console.ReadLine();
            if (cont is null) break;
            sb.Append(' ');
            sb.Append(cont.Trim());
        }
        var trimmed = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            continue;

        // :load <path>
        if (trimmed.StartsWith(":load", StringComparison.OrdinalIgnoreCase))
        {
            var path = trimmed[5..].Trim();
            if (path.Length == 0)
                Console.WriteLine("  Usage: :load <file.ski>");
            else
                LoadFile(path, env, silent: false, maxSteps: maxSteps);
            continue;
        }

        // :env [pattern]
        if (trimmed.StartsWith(":env", StringComparison.OrdinalIgnoreCase))
        {
            var pat = trimmed[4..].Trim();
            PrintEnv(env, pat.Length > 0 ? pat : null);
            continue;
        }

        // :reset  — clear user env and reload init.ski
        if (trimmed.Equals(":reset", StringComparison.OrdinalIgnoreCase))
        {
            env.Clear();
            if (initPath is not null)
            {
                LoadFile(initPath, env, silent: true, maxSteps: maxSteps);
                Cwl($"  Reset — reloaded {initPath} ({env.Count} definition(s))", ConsoleColor.DarkCyan);
            }
            else
            {
                Cwl("  Reset — environment cleared", ConsoleColor.DarkCyan);
            }
            continue;
        }

        // :trace  — toggle step-by-step trace output
        if (trimmed.Equals(":trace", StringComparison.OrdinalIgnoreCase))
        {
            traceMode = !traceMode;
            Cwl($"  Trace : {(traceMode ? "ON" : "OFF")}",
                traceMode ? ConsoleColor.Green : ConsoleColor.DarkGray);
            continue;
        }

        // :expand <expr>  — show fully expanded SKI tree without reducing
        if (trimmed.StartsWith(":expand", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[7..].Trim();
            if (rest.Length == 0)
                Cwl("  Usage: :expand <expr>", ConsoleColor.DarkGray);
            else
                ExpandLine(rest, env);
            continue;
        }

        // :nat <expr>  — reduce and decode as Church numeral
        if (trimmed.StartsWith(":nat", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[4..].Trim();
            if (rest.Length == 0)
                Cwl("  Usage: :nat <expr>", ConsoleColor.DarkGray);
            else
                NatLine(rest, env, maxSteps);
            continue;
        }

        // :bool <expr>  — reduce and decode as Church boolean
        if (trimmed.StartsWith(":bool", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[5..].Trim();
            if (rest.Length == 0)
                Cwl("  Usage: :bool <expr>", ConsoleColor.DarkGray);
            else
                BoolLine(rest, env, maxSteps);
            continue;
        }

        // :undef <name>  — remove a definition from the environment
        if (trimmed.StartsWith(":undef", StringComparison.OrdinalIgnoreCase))
        {
            var name = trimmed[6..].Trim();
            if (name.Length == 0)
                Cwl("  Usage: :undef <name>", ConsoleColor.DarkGray);
            else if (Combinators.IsBuiltin(name))
                Cwl($"  Error  : Cannot undefine built-in '{name}'.", ConsoleColor.Red);
            else if (!env.ContainsKey(name))
                Cwl($"  Error  : '{name}' is not defined.", ConsoleColor.DarkYellow);
            else
            {
                env.Remove(name);
                Cwl($"  Removed {name}", ConsoleColor.DarkGray);
            }
            continue;
        }

        // :bench <expr>  — reduce and report step count
        if (trimmed.StartsWith(":bench", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[6..].Trim();
            if (rest.Length == 0)
                Cwl("  Usage: :bench <expr>", ConsoleColor.DarkGray);
            else
                BenchLine(rest, env, maxSteps);
            continue;
        }

        // :list <expr>  — reduce and decode as Church list
        if (trimmed.StartsWith(":list", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[5..].Trim();
            if (rest.Length == 0)
                Cwl("  Usage: :list <expr>", ConsoleColor.DarkGray);
            else
                ListLine(rest, env, maxSteps);
            continue;
        }

        // :def <name>  — show the definition of a single name
        if (trimmed.StartsWith(":def", StringComparison.OrdinalIgnoreCase))
        {
            var name = trimmed[4..].Trim();
            if (name.Length == 0)
                Cwl("  Usage: :def <name>", ConsoleColor.DarkGray);
            else if (Combinators.IsBuiltin(name))
                Cwl($"  {name} is a built-in combinator", ConsoleColor.Cyan);
            else if (env.TryGetValue(name, out var defExpr))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"  {name}");
                Console.ResetColor();
                Console.WriteLine($" = {defExpr}");
            }
            else
                Cwl($"  '{name}' is not defined", ConsoleColor.DarkYellow);
            continue;
        }

        // :reload  — re-read init.ski into the current environment (without clearing)
        if (trimmed.Equals(":reload", StringComparison.OrdinalIgnoreCase))
        {
            if (initPath is null)
                Cwl("  No init.ski found to reload", ConsoleColor.DarkYellow);
            else
            {
                int before = env.Count;
                LoadFile(initPath, env, silent: true, maxSteps: maxSteps);
                Cwl($"  Reloaded {initPath} ({env.Count - before} new, {env.Count} total)", ConsoleColor.DarkCyan);
            }
            continue;
        }

        // :save <file>  — write all current definitions to a .ski file
        if (trimmed.StartsWith(":save", StringComparison.OrdinalIgnoreCase))
        {
            var path = trimmed[5..].Trim();
            if (path.Length == 0)
                Cwl("  Usage: :save <file.ski>", ConsoleColor.DarkGray);
            else
                SaveEnv(path, env);
            continue;
        }

        // :help
        if (trimmed.Equals(":help", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals(":?", StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp(initPath);
            continue;
        }

        // :set steps N  — configure reduction step limit
        if (trimmed.StartsWith(":set", StringComparison.OrdinalIgnoreCase))
        {
            var setParts = trimmed[4..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (setParts.Length == 2 &&
                setParts[0].Equals("steps", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(setParts[1], out var newLimit) && newLimit > 0)
            {
                maxSteps = newLimit;
                Cwl($"  Step limit set to {maxSteps:N0}", ConsoleColor.DarkCyan);
            }
            else
            {
                Cwl("  Usage: :set steps <N>   (e.g. :set steps 5000000)", ConsoleColor.DarkGray);
            }
            continue;
        }

        RunLine(trimmed, env, traceMode, maxSteps);
    }
}

static void PrintHelp(string? initPath)
{
    Console.WriteLine("SKI(BCWY) Combinator Interpreter");
    Console.WriteLine("Syntax : (f a b c) = left-assoc application  |  (f a) = binary");
    Console.WriteLine("Lambda : \\x y. body  compiled to SKI via bracket abstraction");
    Console.WriteLine("Let    : let x = e1 in e2  local binding (sugar for (\\x. e2) e1)");
    Console.WriteLine("Letrec : letrec f x = body in e2  recursive binding (sugar for let f = Y (\\f x. body))");;
    Console.WriteLine("Nums   : 0  42  etc.  converted to Church numerals at parse time");
    Console.WriteLine("Atoms  : S  K  I  B  C  W  Y  <Name>");
    Console.WriteLine("Define : Name = expr            simple definition");
    Console.WriteLine("         Name x y = expr        parameterized  (= Name = \\x y. expr)");
    Console.WriteLine("Rules  : I x=x  K x y=x  S x y z=(xz)(yz)");
    Console.WriteLine("         B x y z=x(yz)  C x y z=xzy  W x y=xyy  Y f=f(Yf)");
    Console.WriteLine("Import : import <path>  inside .ski files, loads another file");
    Console.WriteLine("Commands:");
    Console.WriteLine("  :load <file>    load definitions/expressions from a .ski file");
    Console.WriteLine("  :save <file>    save all current definitions to a .ski file");
    Console.WriteLine("  :env [pat]      list defined names (optionally filtered)");
    Console.WriteLine("  :def <name>     show definition of a single name");
    Console.WriteLine("  :undef <name>   remove a definition from the environment");
    Console.WriteLine("  :expand <expr>  show fully-expanded SKI tree (no reduction)");
    Console.WriteLine("  :nat <expr>     reduce and decode result as a Church numeral");
    Console.WriteLine("  :bool <expr>    reduce and decode result as a Church boolean");
    Console.WriteLine("  :list <expr>    reduce and decode result as a Church list");
    Console.WriteLine("  :bench <expr>   reduce and report the number of steps taken");
    Console.WriteLine("  :trace          toggle step-by-step reduction trace");
    Console.WriteLine("  :set steps N    set reduction step limit (default: 1,000,000)");
    Console.WriteLine("  :reset          clear user definitions and reload init.ski");
    Console.WriteLine("  :reload         re-read init.ski into current environment");
    Console.WriteLine("  :help           show this message");
    Console.WriteLine("  Tip: end a line with \\ to continue on the next line");
    Console.WriteLine("  Tip: use Up/Down arrows for history, Tab for completion");
    Console.WriteLine("  Tip: results are auto-decoded as Bool/Nat/List when possible (shown as Hint)");
    Console.WriteLine("  exit            quit");
    if (initPath is not null)
        Console.WriteLine($"Loaded : {initPath}");
    Console.WriteLine();
}

static void RunLine(string input, Dictionary<string, Expr> env, bool trace, int maxSteps)
{
    try
    {
        // Detect   Name = expr   or   Name x y = expr   (parameterized definition).
        // But NOT let-expressions, which also contain '='.
        var eqIdx = input.IndexOf('=');
        if (eqIdx > 0 && !input.TrimStart().StartsWith("let ", StringComparison.OrdinalIgnoreCase)
                      && !input.TrimStart().Equals("let", StringComparison.OrdinalIgnoreCase)
                      && !input.TrimStart().StartsWith("letrec ", StringComparison.OrdinalIgnoreCase)
                      && !input.TrimStart().Equals("letrec", StringComparison.OrdinalIgnoreCase))
        {
            var lhsParts = input[..eqIdx].Trim()
                .Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (lhsParts.Length > 0 && lhsParts.All(IsIdent))
            {
                var name  = lhsParts[0];
                var parms = lhsParts[1..];
                if (Combinators.IsBuiltin(name))
                    throw new InvalidOperationException($"Cannot redefine built-in combinator '{name}'.");
                foreach (var p in parms)
                    if (Combinators.IsBuiltin(p))
                        throw new FormatException($"Cannot use built-in '{p}' as a parameter name.");
                var bodyPart   = input[(eqIdx + 1)..].Trim();
                var bodyTokens = Lexer.Tokenize(bodyPart);
                var body       = Parser.Parse(bodyTokens, env);
                if (bodyTokens.Count > 0)
                    throw new FormatException("Unexpected tokens after definition body.");
                var fullBody = parms.Length > 0
                    ? BracketAbstract.AbstractAll(parms, body)
                    : body;
                if (parms.Length == 0 && Expander.ContainsName(body, name))
                    Cwl($"  Warning: '{name}' references itself — use Y for recursion", ConsoleColor.Yellow);
                env[name] = fullBody;
                if (parms.Length > 0)
                    Cwl($"  Defined {name} {string.Join(" ", parms)} = {body}  →  {fullBody}", ConsoleColor.DarkGreen);
                else
                    Cwl($"  Defined {name} = {fullBody}", ConsoleColor.DarkGreen);
                return;
            }
        }

        // Otherwise it's an expression to reduce.
        var tokens = Lexer.Tokenize(input);
        var expr = Parser.Parse(tokens, env);
        if (tokens.Count > 0)
            throw new FormatException("Unexpected tokens after expression.");
        Cwl($"  Parsed : {expr}", ConsoleColor.DarkGray);
        var result = Reducer.Reduce(expr, env, trace ? new ColorWriter(ConsoleColor.DarkGray) : null, maxSteps);
        Cwl($"  Result : {result}", ConsoleColor.Cyan);
        TryPrintHints(result, env, maxSteps);
    }
    catch (Exception ex)
    {
        Cwl($"  Error  : {ex.Message}", ConsoleColor.Red);
    }
}

static void ExpandLine(string input, Dictionary<string, Expr> env)
{
    try
    {
        var tokens = Lexer.Tokenize(input);
        var expr = Parser.Parse(tokens, env);
        if (tokens.Count > 0)
            throw new FormatException("Unexpected tokens after expression.");
        var expanded = Expander.Expand(expr, env);
        Cwl($"  Expanded: {expanded}", ConsoleColor.Cyan);
    }
    catch (Exception ex)
    {
        Cwl($"  Error  : {ex.Message}", ConsoleColor.Red);
    }
}

static void NatLine(string input, Dictionary<string, Expr> env, int maxSteps)
{
    try
    {
        var tokens = Lexer.Tokenize(input);
        var expr = Parser.Parse(tokens, env);
        if (tokens.Count > 0)
            throw new FormatException("Unexpected tokens after expression.");
        var result = Reducer.Reduce(expr, env, null, maxSteps);
        Cwl($"  Result : {result}", ConsoleColor.DarkGray);
        var n = ChurchNat.Decode(result, env, maxSteps);
        if (n is not null)
            Cwl($"  Nat    : {n}", ConsoleColor.Green);
        else
            Cwl("  Nat    : (not a Church numeral or exceeds decode limit)", ConsoleColor.DarkYellow);
    }
    catch (Exception ex)
    {
        Cwl($"  Error  : {ex.Message}", ConsoleColor.Red);
    }
}

static void BoolLine(string input, Dictionary<string, Expr> env, int maxSteps)
{
    try
    {
        var tokens = Lexer.Tokenize(input);
        var expr = Parser.Parse(tokens, env);
        if (tokens.Count > 0)
            throw new FormatException("Unexpected tokens after expression.");
        var result = Reducer.Reduce(expr, env, null, maxSteps);
        Cwl($"  Result : {result}", ConsoleColor.DarkGray);
        var b = ChurchBool.Decode(result, env, maxSteps);
        if (b is true)
            Cwl("  Bool   : TRUE", ConsoleColor.Green);
        else if (b is false)
            Cwl("  Bool   : FALSE", ConsoleColor.Yellow);
        else
            Cwl("  Bool   : (not a Church boolean)", ConsoleColor.DarkYellow);
    }
    catch (Exception ex)
    {
        Cwl($"  Error  : {ex.Message}", ConsoleColor.Red);
    }
}

static void ListLine(string input, Dictionary<string, Expr> env, int maxSteps)
{
    try
    {
        var tokens = Lexer.Tokenize(input);
        var expr   = Parser.Parse(tokens, env);
        if (tokens.Count > 0)
            throw new FormatException("Unexpected tokens after expression.");
        var result = Reducer.Reduce(expr, env, null, maxSteps);
        Cwl($"  Result : {result}", ConsoleColor.DarkGray);
        var items = ChurchList.Decode(result, env, maxSteps);
        if (items is not null)
            Cwl($"  List   : [{string.Join(", ", items)}]  (length {items.Count})",
                ConsoleColor.Green);
        else
            Cwl("  List   : (not a Church list, or ISNIL/HEAD/TAIL not defined)",
                ConsoleColor.DarkYellow);
    }
    catch (Exception ex)
    {
        Cwl($"  Error  : {ex.Message}", ConsoleColor.Red);
    }
}

static void BenchLine(string input, Dictionary<string, Expr> env, int maxSteps)
{
    try
    {
        var tokens = Lexer.Tokenize(input);
        var expr = Parser.Parse(tokens, env);
        if (tokens.Count > 0)
            throw new FormatException("Unexpected tokens after expression.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (result, steps) = Reducer.ReduceWithCount(expr, env, maxSteps);
        sw.Stop();
        Cwl($"  Result : {result}", ConsoleColor.Cyan);
        TryPrintHints(result, env, maxSteps);
        Cwl($"  Steps  : {steps:N0}  ({sw.ElapsedMilliseconds} ms)", ConsoleColor.DarkGray);
    }
    catch (Exception ex)
    {
        Cwl($"  Error  : {ex.Message}", ConsoleColor.Red);
    }
}

static void SaveEnv(string path, Dictionary<string, Expr> env)
{
    try
    {
        var lines = env
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key} = {kv.Value}");
        File.WriteAllLines(path, lines);
        Cwl($"  Saved {env.Count} definition(s) to '{path}'", ConsoleColor.DarkGray);
    }
    catch (Exception ex)
    {
        Cwl($"  Error  : {ex.Message}", ConsoleColor.Red);
    }
}

static bool IsIdent(string s) =>
    s.Length > 0 && char.IsLetter(s[0]) &&
    s.All(c => char.IsLetterOrDigit(c) || c == '_');

/// <summary>After a successful reduction, attempts to decode the result as a Church
/// boolean, numeral, or list and prints a Hint line for each that matches.</summary>
static void TryPrintHints(Expr result, Dictionary<string, Expr> env, int maxSteps)
{
    // Cap decode budget so hints never noticeably slow down the REPL.
    const int hintStepCap = 100_000;
    int steps = Math.Min(maxSteps, hintStepCap);
    var hints = new List<string>(3);

    try
    {
        var b = ChurchBool.Decode(result, env, steps);
        if (b is true)  hints.Add("Bool TRUE");
        else if (b is false) hints.Add("Bool FALSE");
    }
    catch { }

    try
    {
        var n = ChurchNat.Decode(result, env, steps);
        if (n is not null) hints.Add($"Nat {n}");
    }
    catch { }

    if (env.ContainsKey("ISNIL") && env.ContainsKey("HEAD") && env.ContainsKey("TAIL"))
    {
        try
        {
            var items = SKI.ChurchList.Decode(result, env, steps);
            if (items is not null)
                hints.Add($"List [{string.Join(", ", items)}]  (length {items.Count})");
        }
        catch { }
    }

    if (hints.Count > 0)
        Cwl($"  Hint   : {string.Join("  /  ", hints)}", ConsoleColor.DarkMagenta);
}

static void Cwl(string text, ConsoleColor color)
{
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ResetColor();
}

/// <summary>Loads and executes every non-blank, non-comment line in a .ski file.
/// Supports <c>import &lt;path&gt;</c> directives to load other .ski files relative to this one.</summary>
static void LoadFile(string path, Dictionary<string, Expr> env, bool silent, int maxSteps,
                     HashSet<string>? loadStack = null)
{
    path = Path.GetFullPath(path);
    if (!File.Exists(path))
    {
        Cwl($"  Error  : File not found: '{path}'", ConsoleColor.Red);
        return;
    }
    // Guard against circular imports
    loadStack ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (!loadStack.Add(path))
    {
        Cwl($"  Warning: circular import ignored: '{path}'", ConsoleColor.DarkYellow);
        return;
    }
    var fileDir = Path.GetDirectoryName(path) ?? ".";
    int defined = 0, errors = 0;
    foreach (var raw in File.ReadLines(path))
    {
        // Strip inline comments and trim whitespace
        var commentIdx = raw.IndexOf('#');
        var line = (commentIdx >= 0 ? raw[..commentIdx] : raw).Trim();
        if (line.Length == 0) continue;
        try
        {
            // import <path>  — load another .ski file relative to this one
            if (line.StartsWith("import ", StringComparison.OrdinalIgnoreCase))
            {
                var importPath = line[7..].Trim();
                if (!Path.IsPathRooted(importPath))
                    importPath = Path.Combine(fileDir, importPath);
                LoadFile(importPath, env, silent, maxSteps, loadStack);
                continue;
            }
            // Definitions: Name = expr  or  Name x y = expr  (parameterized).
            // Exclude let-expressions which also contain '='.
            var eqIdx = line.IndexOf('=');
            if (eqIdx > 0 && !line.StartsWith("let ", StringComparison.OrdinalIgnoreCase)
                          && !line.Equals("let", StringComparison.OrdinalIgnoreCase)
                          && !line.StartsWith("letrec ", StringComparison.OrdinalIgnoreCase)
                          && !line.Equals("letrec", StringComparison.OrdinalIgnoreCase))
            {
                var lhsParts = line[..eqIdx].Trim()
                    .Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (lhsParts.Length > 0 && lhsParts.All(IsIdent))
                {
                    var name  = lhsParts[0];
                    var parms = lhsParts[1..];
                    if (Combinators.IsBuiltin(name))
                        throw new InvalidOperationException($"Cannot redefine built-in combinator '{name}'.");
                    foreach (var p in parms)
                        if (Combinators.IsBuiltin(p))
                            throw new FormatException($"Cannot use built-in '{p}' as a parameter name.");
                    var bodyPart   = line[(eqIdx + 1)..].Trim();
                    var bodyTokens = Lexer.Tokenize(bodyPart);
                    var body       = Parser.Parse(bodyTokens, env);
                    if (bodyTokens.Count > 0)
                        throw new FormatException("Unexpected tokens after definition body.");
                    var fullBody = parms.Length > 0
                        ? BracketAbstract.AbstractAll(parms, body)
                        : body;
                    env[name] = fullBody;
                    if (!silent) Cwl($"  Defined {name} = {fullBody}", ConsoleColor.DarkGreen);
                    defined++;
                    continue;
                }
            }
            // Non-definition lines in a file: evaluate and print.
            RunLine(line, env, false, maxSteps);
        }
        catch (Exception ex)
        {
            Cwl($"  Error  : {path}: {ex.Message}", ConsoleColor.Red);
            errors++;
        }
    }
    loadStack.Remove(path);
    if (!silent)
        Cwl($"  Loaded {path} — {defined} definition(s){(errors > 0 ? $", {errors} error(s)" : "")}", ConsoleColor.DarkGray);
}

/// <summary>Searches for init.ski next to the exe, then in the working directory.</summary>
static string? FindInitFile()
{
    var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
    foreach (var candidate in new[] { Path.Combine(exeDir, "init.ski"), "init.ski" })
        if (File.Exists(candidate)) return candidate;
    return null;
}

/// <summary>Prints all user-defined names, optionally filtered by a substring pattern.</summary>
static void PrintEnv(Dictionary<string, Expr> env, string? pattern)
{
    var entries = env
        .Where(kv => pattern is null ||
                     kv.Key.Contains(pattern, StringComparison.OrdinalIgnoreCase))
        .OrderBy(kv => kv.Key)
        .ToList();

    if (entries.Count == 0)
    {
        Cwl(pattern is null
            ? "  (no definitions)"
            : $"  (no definitions matching '{pattern}')", ConsoleColor.DarkGray);
        return;
    }

    int nameWidth = entries.Max(kv => kv.Key.Length);
    foreach (var (name, body) in entries)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"  {name.PadRight(nameWidth)}");
        Console.ResetColor();
        Console.WriteLine($" = {body}");
    }

    Cwl($"  ---\n  {entries.Count} definition(s)" +
        (pattern is not null ? $" matching '{pattern}'" : ""), ConsoleColor.DarkGray);
}

namespace SKI
{
    // ── Expressions ──────────────────────────────────────────────────────────

    abstract record Expr
    {
        public sealed override string ToString() => Print(this);

        private static string Print(Expr e) => e switch
        {
            Combinator c    => c.Name.ToString(),
            NameRef nr      => nr.Name,
            App(var f, var x) => $"({Print(f)} {Print(x)})",
            _ => "?"
        };
    }

    record Combinator(char Name) : Expr;
    record NameRef(string Name)  : Expr;
    record App(Expr Fun, Expr Arg) : Expr;

    static class Combinators
    {
        public static readonly Combinator S = new('S');
        public static readonly Combinator K = new('K');
        public static readonly Combinator I = new('I');
        public static readonly Combinator B = new('B');
        public static readonly Combinator C = new('C');
        public static readonly Combinator W = new('W');
        public static readonly Combinator Y = new('Y');

        private static readonly Dictionary<string, Combinator> Builtins = new(StringComparer.Ordinal)
        {
            ["S"] = S, ["K"] = K, ["I"] = I,
            ["B"] = B, ["C"] = C, ["W"] = W, ["Y"] = Y
        };

        public static bool IsBuiltin(string name) => Builtins.ContainsKey(name);
        public static Combinator FromName(string name) => Builtins[name];
    }

    // ── Tokens ────────────────────────────────────────────────────────────────

    enum TokenKind { LParen, RParen, Ident, Lambda, Dot, Number, Let, Letrec, In, Eq }
    record Token(TokenKind Kind, string Value, int Position);

    // ── Lexer ─────────────────────────────────────────────────────────────────

    static class Lexer
    {
        public static Queue<Token> Tokenize(string source)
        {
            var q = new Queue<Token>();
            int i = 0;
            while (i < source.Length)
            {
                char ch = source[i];
                if (char.IsWhiteSpace(ch)) { i++; continue; }
                if (ch == '(') { q.Enqueue(new Token(TokenKind.LParen, "(", i++)); continue; }
                if (ch == ')') { q.Enqueue(new Token(TokenKind.RParen, ")", i++)); continue; }
                if (ch == '\\') { q.Enqueue(new Token(TokenKind.Lambda, "\\", i++)); continue; }
                if (ch == '.')  { q.Enqueue(new Token(TokenKind.Dot,    ".",  i++)); continue; }
                if (ch == '=')  { q.Enqueue(new Token(TokenKind.Eq,     "=",  i++)); continue; }
                if (char.IsDigit(ch))
                {
                    int start = i;
                    while (i < source.Length && char.IsDigit(source[i])) i++;
                    q.Enqueue(new Token(TokenKind.Number, source[start..i], start));
                    continue;
                }
                if (char.IsLetter(ch) || ch == '_')
                {
                    int start = i;
                    while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                        i++;
                    var word = source[start..i];
                    var kind = word switch
                    {
                        "let"    => TokenKind.Let,
                        "letrec" => TokenKind.Letrec,
                        "in"     => TokenKind.In,
                        _        => TokenKind.Ident
                    };
                    q.Enqueue(new Token(kind, word, start));
                    continue;
                }
                throw new FormatException($"Unexpected character '{ch}' at column {i + 1}.");
            }
            return q;
        }
    }

    // ── Parser ────────────────────────────────────────────────────────────────

    static class Parser
    {
        public static Expr Parse(Queue<Token> tokens, Dictionary<string, Expr> env)
        {
            if (tokens.Count == 0)
                throw new FormatException("Unexpected end of input.");

            var tok = tokens.Dequeue();

            return tok.Kind switch
            {
                TokenKind.Ident   => Combinators.IsBuiltin(tok.Value)
                                        ? Combinators.FromName(tok.Value)
                                        : (Expr)new NameRef(tok.Value),
                TokenKind.LParen  => ParseApp(tokens, env, tok.Position),
                TokenKind.RParen  => throw new FormatException($"Unexpected ')' at column {tok.Position + 1}."),
                TokenKind.Lambda  => ParseLambda(tokens, env),
                TokenKind.Number  => BuildChurchNumeral(int.Parse(tok.Value)),
                TokenKind.Let     => ParseLet(tokens, env, tok.Position),
                TokenKind.Letrec  => ParseLetRec(tokens, env, tok.Position),
                _ => throw new FormatException($"Unexpected token '{tok.Value}' at column {tok.Position + 1}.")
            };
        }

        private static Expr ParseApp(Queue<Token> tokens, Dictionary<string, Expr> env, int openParenPos)
        {
            // Parse the function and at least one argument, then keep consuming
            // additional arguments until ')' — left-associative: (f a b c) = (((f a) b) c).
            var result = Parse(tokens, env);

            if (tokens.Count == 0 || tokens.Peek().Kind == TokenKind.RParen)
                throw new FormatException($"Application requires at least one argument (column {openParenPos + 1}).");

            while (tokens.Count > 0 && tokens.Peek().Kind != TokenKind.RParen)
                result = new App(result, Parse(tokens, env));

            if (tokens.Count == 0)
                throw new FormatException($"Expected ')' to close '(' at column {openParenPos + 1}.");
            tokens.Dequeue(); // consume ')'

            return result;
        }

        private static Expr ParseLambda(Queue<Token> tokens, Dictionary<string, Expr> env)
        {
            // Collect parameter names until a Dot token.
            var parms = new List<string>();
            while (tokens.Count > 0 && tokens.Peek().Kind == TokenKind.Ident)
            {
                var p = tokens.Dequeue();
                if (Combinators.IsBuiltin(p.Value))
                    throw new FormatException($"Cannot use built-in '{p.Value}' as a lambda parameter.");
                parms.Add(p.Value);
            }
            if (parms.Count == 0)
                throw new FormatException("Lambda requires at least one parameter name before '.'.");
            if (tokens.Count == 0 || tokens.Peek().Kind != TokenKind.Dot)
                throw new FormatException("Expected '.' after lambda parameters.");
            tokens.Dequeue(); // consume '.'
            var body = Parse(tokens, env);
            return BracketAbstract.AbstractAll(parms, body);
        }

        // let x = e1 in e2  →  (\x. e2) e1
        // Also supports:  let f x y = body in e2  (parameterized, like top-level defs)
        private static Expr ParseLet(Queue<Token> tokens, Dictionary<string, Expr> env, int letPos)
        {
            // Collect binder name
            if (tokens.Count == 0 || tokens.Peek().Kind != TokenKind.Ident)
                throw new FormatException($"Expected name after 'let' (column {letPos + 1}).");
            var name = tokens.Dequeue().Value;
            if (Combinators.IsBuiltin(name))
                throw new FormatException($"Cannot use built-in '{name}' as a let binder.");

            // Optional parameters: let f x y = ...
            var parms = new List<string>();
            while (tokens.Count > 0 && tokens.Peek().Kind == TokenKind.Ident)
            {
                var p = tokens.Dequeue();
                if (Combinators.IsBuiltin(p.Value))
                    throw new FormatException($"Cannot use built-in '{p.Value}' as a parameter.");
                parms.Add(p.Value);
            }

            // Consume '='
            if (tokens.Count == 0 || tokens.Peek().Kind != TokenKind.Eq)
                throw new FormatException($"Expected '=' in let binding (near column {letPos + 1}).");
            tokens.Dequeue();

            // Parse bound expression
            var e1Raw = Parse(tokens, env);
            var e1 = parms.Count > 0 ? BracketAbstract.AbstractAll(parms, e1Raw) : e1Raw;

            // Consume 'in'
            if (tokens.Count == 0 || tokens.Peek().Kind != TokenKind.In)
                throw new FormatException($"Expected 'in' after let binding (near column {letPos + 1}).");
            tokens.Dequeue();

            // Parse body
            var e2 = Parse(tokens, env);

            // Desugar: ((\name. e2) e1)
            var lambda = BracketAbstract.Abstract(name, e2);
            return new App(lambda, e1);
        }

        // letrec f x y = body in e2
        //   →  (\f. e2) (Y (\f x y. body))
        // The name is abstracted over the body so Y provides the self-reference.
        private static Expr ParseLetRec(Queue<Token> tokens, Dictionary<string, Expr> env, int letrecPos)
        {
            if (tokens.Count == 0 || tokens.Peek().Kind != TokenKind.Ident)
                throw new FormatException($"Expected name after 'letrec' (column {letrecPos + 1}).");
            var name = tokens.Dequeue().Value;
            if (Combinators.IsBuiltin(name))
                throw new FormatException($"Cannot use built-in '{name}' as a letrec binder.");

            var parms = new List<string>();
            while (tokens.Count > 0 && tokens.Peek().Kind == TokenKind.Ident)
            {
                var p = tokens.Dequeue();
                if (Combinators.IsBuiltin(p.Value))
                    throw new FormatException($"Cannot use built-in '{p.Value}' as a parameter.");
                parms.Add(p.Value);
            }

            if (tokens.Count == 0 || tokens.Peek().Kind != TokenKind.Eq)
                throw new FormatException($"Expected '=' in letrec binding (near column {letrecPos + 1}).");
            tokens.Dequeue();

            var e1Raw = Parse(tokens, env);

            // Build \name parms. body  then wrap in Y
            var allParms = new List<string>(parms.Count + 1) { name };
            allParms.AddRange(parms);
            var e1  = BracketAbstract.AbstractAll(allParms, e1Raw);
            var yE1 = new App(Combinators.Y, e1);

            if (tokens.Count == 0 || tokens.Peek().Kind != TokenKind.In)
                throw new FormatException($"Expected 'in' after letrec binding (near column {letrecPos + 1}).");
            tokens.Dequeue();

            var e2     = Parse(tokens, env);
            var lambda = BracketAbstract.Abstract(name, e2);
            return new App(lambda, yE1);
        }

        // ZERO = K I,  ONE = I,  n = S B (n-1) for n >= 2.
        // Satisfies: n f x = f^n x  (Church numeral encoding).
        private static Expr BuildChurchNumeral(int n)
        {
            if (n == 0) return new App(Combinators.K, Combinators.I);
            Expr result = Combinators.I;
            for (int i = 2; i <= n; i++)
                result = new App(new App(Combinators.S, Combinators.B), result);
            return result;
        }
    }

    // ── Expander ──────────────────────────────────────────────────────────────

    static class Expander
    {
        /// <summary>Recursively substitutes all NameRef nodes with their stored definitions.
        /// Used by :expand command and :nat decoder — NOT called before normal reduction.</summary>
        public static Expr Expand(Expr expr, Dictionary<string, Expr> env,
                                  HashSet<string>? expanding = null)
        {
            switch (expr)
            {
                case NameRef(var name):
                    if (!env.TryGetValue(name, out var def))
                        throw new InvalidOperationException($"Undefined name '{name}'.");
                    if (expanding is not null && expanding.Contains(name))
                        throw new InvalidOperationException($"Cyclic definition involving '{name}'.");
                    var seen = expanding is null
                        ? new HashSet<string>(StringComparer.Ordinal) { name }
                        : new HashSet<string>(expanding, StringComparer.Ordinal) { name };
                    return Expand(def, env, seen);

                case App(var f, var x):
                    return new App(Expand(f, env, expanding), Expand(x, env, expanding));

                default:
                    return expr;   // Combinator — nothing to expand
            }
        }

        /// <summary>Returns true if <paramref name="expr"/> contains a direct NameRef to <paramref name="name"/>.</summary>
        public static bool ContainsName(Expr expr, string name) => expr switch
        {
            NameRef nr      => nr.Name == name,
            App(var f, var x) => ContainsName(f, name) || ContainsName(x, name),
            _               => false
        };
    }

    // ── Bracket abstraction ───────────────────────────────────────────────────

    /// <summary>
    /// Turner-optimized bracket abstraction: compiles lambda expressions to SKI combinators.
    /// Uses B and C to avoid redundant K-wrapping, and eta-reduces (f x) → f when x ∉ fv(f).
    /// </summary>
    static class BracketAbstract
    {
        public static bool IsFree(string x, Expr expr) => expr switch
        {
            NameRef(var n)    => StringComparer.Ordinal.Equals(n, x),
            App(var f, var a) => IsFree(x, f) || IsFree(x, a),
            _                 => false  // Combinator — never contains free variables
        };

        /// <summary>Compiles [x] body into a combinator expression without x.</summary>
        public static Expr Abstract(string x, Expr body)
        {
            // [x] x  →  I
            if (body is NameRef(var n) && StringComparer.Ordinal.Equals(n, x))
                return Combinators.I;

            // x not free  →  K body
            if (!IsFree(x, body))
                return new App(Combinators.K, body);

            if (body is App(var f, var a))
            {
                // Eta: [x] (f x)  →  f   when x ∉ fv(f)
                if (a is NameRef(var an) && StringComparer.Ordinal.Equals(an, x) && !IsFree(x, f))
                    return f;

                // B: [x] (f a)  →  B f ([x] a)   when x ∉ fv(f)
                if (!IsFree(x, f))
                    return new App(new App(Combinators.B, f), Abstract(x, a));

                // C: [x] (f a)  →  C ([x] f) a   when x ∉ fv(a)
                if (!IsFree(x, a))
                    return new App(new App(Combinators.C, Abstract(x, f)), a);

                // S: [x] (f a)  →  S ([x] f) ([x] a)
                return new App(new App(Combinators.S, Abstract(x, f)), Abstract(x, a));
            }

            return new App(Combinators.K, body); // unreachable
        }

        /// <summary>Abstracts multiple params left-to-right: \p0 p1 p2. body = [p0]([p1]([p2] body)).</summary>
        public static Expr AbstractAll(IReadOnlyList<string> parms, Expr body)
        {
            var result = body;
            for (int i = parms.Count - 1; i >= 0; i--)
                result = Abstract(parms[i], result);
            return result;
        }
    }

    // ── Reducer ───────────────────────────────────────────────────────────────

    static class Reducer
    {
        public const int DefaultMaxSteps = 1_000_000;

        /// <summary>Fully reduces an expression using graph reduction with sharing.
        /// The immutable <see cref="Expr"/> tree is first converted to a mutable graph,
        /// reduced in-place (sharing sub-graphs), then converted back for display.
        /// <paramref name="trace"/> receives each intermediate expression if non-null.</summary>
        public static Expr Reduce(Expr expr, Dictionary<string, Expr> env, TextWriter? trace,
                                  int maxSteps = DefaultMaxSteps)
        {
            var root = Graph.FromExpr(expr, env);
            int step = 0;
            for (; step < maxSteps; step++)
            {
                if (!Graph.Step(root, env))
                    break;   // normal form reached
                if (trace is not null)
                    trace.WriteLine($"  [{step + 1,6}] {Graph.ToExpr(root)}");
            }
            if (step == maxSteps)
                throw new InvalidOperationException($"Reduction did not terminate after {maxSteps} steps.");
            return Graph.ToExpr(root);
        }

        /// <summary>Like <see cref="Reduce"/> but also returns the step count.</summary>
        public static (Expr Result, int Steps) ReduceWithCount(Expr expr, Dictionary<string, Expr> env,
                                                                int maxSteps = DefaultMaxSteps)
        {
            var root = Graph.FromExpr(expr, env);
            int step = 0;
            for (; step < maxSteps; step++)
            {
                if (!Graph.Step(root, env))
                    return (Graph.ToExpr(root), step);
            }
            throw new InvalidOperationException($"Reduction did not terminate after {maxSteps} steps.");
        }
    }

    // ── Graph (mutable sharing graph for reduction) ───────────────────────────

    /// <summary>
    /// A mutable graph node. Each node is one of:
    ///   App  — application, with Fun and Arg pointers (may be redirected)
    ///   Atom — a combinator leaf (S K I B C W Y or sentinel)
    ///   Name — an unresolved name reference (resolved lazily)
    ///   Ind  — indirection: transparently forwards to another node (enables sharing)
    ///
    /// Reduction is done by the spine-walking algorithm (STG-style):
    ///   1. Walk down the left spine collecting App nodes.
    ///   2. When enough arguments are on the spine, fire the redex in-place.
    ///   3. Overwrite the root App node with an Ind to the result (sharing).
    ///   4. Return true if any reduction occurred.
    /// </summary>
    sealed class GNode
    {
        public enum Tag { App, Atom, Name, Ind }
        public Tag Kind;

        // App
        public GNode? Fun;
        public GNode? Arg;

        // Atom (combinator / sentinel)
        public char   AtomName;

        // Name
        public string? NameStr;

        // Ind
        public GNode? Fwd;

        public static GNode MakeApp(GNode f, GNode a) => new() { Kind = Tag.App, Fun = f, Arg = a };
        public static GNode MakeAtom(char c)          => new() { Kind = Tag.Atom, AtomName = c };
        public static GNode MakeName(string n)        => new() { Kind = Tag.Name, NameStr = n };
        public static GNode MakeInd(GNode to)         => new() { Kind = Tag.Ind, Fwd = to };

        /// <summary>Dereference indirections in-place.</summary>
        public static GNode Deref(GNode n)
        {
            while (n.Kind == Tag.Ind) n = n.Fwd!;
            return n;
        }

        /// <summary>Overwrite this node to become an indirection to <paramref name="target"/>.</summary>
        public void Redirect(GNode target)
        {
            target = Deref(target);
            if (target == this) return;
            Kind = Tag.Ind;
            Fwd  = target;
            // Clear other fields to allow GC
            Fun = null; Arg = null; NameStr = null;
        }
    }

    static class Graph
    {
        // Pool of cached atom nodes (one per combinator letter)
        private static readonly Dictionary<char, GNode> AtomPool = new();

        public static GNode Atom(char c)
        {
            if (!AtomPool.TryGetValue(c, out var n))
                AtomPool[c] = n = GNode.MakeAtom(c);
            return n;
        }

        /// <summary>Convert an immutable <see cref="Expr"/> tree to a mutable graph.
        /// <paramref name="env"/> is used to eagerly inline non-recursive NameRefs at the
        /// graph boundary so that sharing kicks in immediately.</summary>
        public static GNode FromExpr(Expr e, Dictionary<string, Expr> env)
        {
            // Use an explicit stack to avoid stack-overflow on deep trees
            return Build(e, env, new Dictionary<Expr, GNode>(ReferenceEqualityComparer.Instance));
        }

        private static GNode Build(Expr e, Dictionary<string, Expr> env,
                                   Dictionary<Expr, GNode> memo)
        {
            if (memo.TryGetValue(e, out var cached)) return cached;

            GNode node;
            switch (e)
            {
                case Combinator c:
                    return Atom(c.Name);   // shared global atom, no memo needed

                case NameRef(var name):
                    node = GNode.MakeName(name);
                    break;

                case App(var f, var a):
                    // Register placeholder before recursing to handle DAGs
                    node = GNode.MakeApp(null!, null!);
                    memo[e] = node;
                    node.Fun = Build(f, env, memo);
                    node.Arg = Build(a, env, memo);
                    return node;

                default:
                    return Atom('?');
            }

            memo[e] = node;
            return node;
        }

        /// <summary>Convert a (possibly-reduced) graph back to an immutable <see cref="Expr"/>
        /// for printing. Handles cycles via a visited set (shouldn't occur in well-typed terms).</summary>
        public static Expr ToExpr(GNode n)
        {
            return Convert(GNode.Deref(n), new HashSet<GNode>(ReferenceEqualityComparer.Instance));
        }

        private static Expr Convert(GNode n, HashSet<GNode> visited)
        {
            n = GNode.Deref(n);

            switch (n.Kind)
            {
                case GNode.Tag.Atom:
                    // Atoms come from AtomPool and are shared across the whole graph.
                    // They can never form a cycle, so bypass the visited check entirely.
                    return new Combinator(n.AtomName);
                case GNode.Tag.Name:
                    return new NameRef(n.NameStr!);
                case GNode.Tag.App:
                    // Track App nodes on the current DFS path to catch true cycles.
                    // Remove after processing so the same node can be re-entered from
                    // a sibling branch (sharing is fine; only back-edges are cycles).
                    if (!visited.Add(n))
                        return new NameRef("…");   // true cycle — should not occur in well-formed terms
                    var f = Convert(GNode.Deref(n.Fun!), visited);
                    var a = Convert(GNode.Deref(n.Arg!), visited);
                    visited.Remove(n);
                    return new App(f, a);
                default:
                    return new NameRef("?");
            }
        }

        /// <summary>
        /// One full outermost-leftmost reduction step on the graph.
        /// Returns true if a step was taken, false if in normal form.
        ///
        /// Algorithm:
        ///   Walk the left spine collecting App nodes.
        ///   When we reach a head (Atom or Name), check if there are enough args.
        ///   If so, fire the redex by overwriting the topmost App with an Ind.
        /// </summary>
        public static bool Step(GNode root, Dictionary<string, Expr> env)
        {
            // spine[0] = topmost App on the way down, spine[last] = nearest App to head
            var spine = new List<GNode>(16);
            var focus = GNode.Deref(root);

            // Walk left spine
            while (focus.Kind == GNode.Tag.App)
            {
                spine.Add(focus);
                focus = GNode.Deref(focus.Fun!);
            }

            // Resolve name lazily
            if (focus.Kind == GNode.Tag.Name)
            {
                if (!env.TryGetValue(focus.NameStr!, out var def))
                    return false;   // undefined — stuck
                var replacement = Build(def, env, new Dictionary<Expr, GNode>(ReferenceEqualityComparer.Instance));
                // Overwrite the Name node to redirect — but it's shared as an atom,
                // so overwrite the nearest App's Fun pointer instead, or overwrite focus
                focus.Kind    = replacement.Kind;
                focus.Fun     = replacement.Fun;
                focus.Arg     = replacement.Arg;
                focus.AtomName= replacement.AtomName;
                focus.NameStr = replacement.NameStr;
                focus.Fwd     = replacement.Fwd;
                return true;
            }

            if (focus.Kind != GNode.Tag.Atom) return false;

            int arity = spine.Count;
            char head = focus.AtomName;

            // Helper: get the Arg of the k-th App from the top of the spine (0 = nearest to head)
            GNode GetArg(int k) => GNode.Deref(spine[spine.Count - 1 - k].Arg!);
            // The redex root = spine[spine.Count - 1 - (needed-1)] — the App that has all args
            GNode RedexRoot(int needed) => spine[spine.Count - needed];

            switch (head)
            {
                case 'I' when arity >= 1:
                {
                    // I x → x   : overwrite (I x) app with indirection to x
                    var rx = RedexRoot(1);
                    rx.Redirect(GetArg(0));
                    return true;
                }
                case 'K' when arity >= 2:
                {
                    // K x y → x  (x is first/deepest arg = GetArg(0))
                    var rx = RedexRoot(2);
                    rx.Redirect(GetArg(0));
                    return true;
                }
                case 'S' when arity >= 3:
                {
                    // S x y z → (x z)(y z)
                    // GetArg(0)=x (first applied), GetArg(1)=y, GetArg(2)=z (last applied)
                    var x = GetArg(0); var y = GetArg(1); var z = GetArg(2);
                    // Share z: both branches point to the same node
                    var newNode = GNode.MakeApp(GNode.MakeApp(x, z), GNode.MakeApp(y, z));
                    RedexRoot(3).Redirect(newNode);
                    return true;
                }
                case 'B' when arity >= 3:
                {
                    // B x y z → x (y z)
                    var x = GetArg(0); var y = GetArg(1); var z = GetArg(2);
                    RedexRoot(3).Redirect(GNode.MakeApp(x, GNode.MakeApp(y, z)));
                    return true;
                }
                case 'C' when arity >= 3:
                {
                    // C x y z → x z y
                    var x = GetArg(0); var y = GetArg(1); var z = GetArg(2);
                    RedexRoot(3).Redirect(GNode.MakeApp(GNode.MakeApp(x, z), y));
                    return true;
                }
                case 'W' when arity >= 2:
                {
                    // W x y → x y y   (y is shared)
                    var x = GetArg(0); var y = GetArg(1);
                    RedexRoot(2).Redirect(GNode.MakeApp(GNode.MakeApp(x, y), y));
                    return true;
                }
                case 'Y' when arity >= 2:
                {
                    // (Y f) x → f (Y f) x    [lazy: only when applied to an arg]
                    // f is first applied arg, x is second
                    var f = GetArg(0); var x = GetArg(1);
                    var yf = GNode.MakeApp(Atom('Y'), f);
                    RedexRoot(2).Redirect(GNode.MakeApp(GNode.MakeApp(f, yf), x));
                    return true;
                }
                default:
                    // Not enough args or unknown combinator — try to normalise inside args
                    // (normal-order: after the head is stuck, reduce leftmost arg)
                    return StepInArgs(spine, env);
            }
        }

        /// <summary>
        /// When the head is stuck (too few args), walk into arguments left-to-right
        /// trying to find a reducible sub-expression (normal-order strategy).
        /// </summary>
        private static bool StepInArgs(List<GNode> spine, Dictionary<string, Expr> env)
        {
            // Traverse spine from innermost to outermost (leftmost-first normal order)
            for (int i = spine.Count - 1; i >= 0; i--)
            {
                var argNode = GNode.Deref(spine[i].Arg!);
                // Try to reduce the arg sub-tree
                if (StepSub(argNode, env))
                    return true;
            }
            return false;
        }

        /// <summary>Recursively step inside a sub-graph (for normal-order reduction of args).</summary>
        private static bool StepSub(GNode n, Dictionary<string, Expr> env)
        {
            n = GNode.Deref(n);
            if (n.Kind == GNode.Tag.App)
                return Step(n, env);
            if (n.Kind == GNode.Tag.Name)
            {
                if (!env.TryGetValue(n.NameStr!, out var def)) return false;
                var rep = Build(def, env, new Dictionary<Expr, GNode>(ReferenceEqualityComparer.Instance));
                n.Kind = rep.Kind; n.Fun = rep.Fun; n.Arg = rep.Arg;
                n.AtomName = rep.AtomName; n.NameStr = rep.NameStr; n.Fwd = rep.Fwd;
                return true;
            }
            return false;
        }
    }

    // ── Church-numeral decoder ────────────────────────────────────────────────

    static class ChurchNat
    {
        private const int MaxN = 10_000;

        public static int? Decode(Expr result, Dictionary<string, Expr> env,
                                  int maxSteps = Reducer.DefaultMaxSteps)
        {
            var tick = new Combinator('\u0001');
            var zero = new Combinator('\u0002');
            Expr applied = new App(new App(result, tick), zero);
            Expr reduced;
            try { reduced = Reducer.Reduce(applied, env, null, maxSteps); }
            catch { return null; }
            return CountTickSpine(reduced, tick, zero);
        }

        private static int? CountTickSpine(Expr e, Combinator tick, Combinator zero)
        {
            int count = 0;
            while (true)
            {
                if (e == zero) return count;
                if (e is App(var f, var inner) && f == tick)
                {
                    count++;
                    if (count > MaxN) return null;
                    e = inner;
                    continue;
                }
                return null;  // unexpected shape
            }
        }
    }

    // ── Church-boolean decoder ────────────────────────────────────────────────

    static class ChurchBool
    {
        public static bool? Decode(Expr result, Dictionary<string, Expr> env,
                                   int maxSteps = Reducer.DefaultMaxSteps)
        {
            var trueS  = new Combinator('\u0003');
            var falseS = new Combinator('\u0004');
            Expr applied = new App(new App(result, trueS), falseS);
            try
            {
                var reduced = Reducer.Reduce(applied, env, null, maxSteps);
                if (reduced == trueS)  return true;
                if (reduced == falseS) return false;
                return null;
            }
            catch { return null; }
        }
    }

    // ── Church-list decoder ───────────────────────────────────────────────────

    static class ChurchList
    {
        private const int MaxLen = 256;

        public static List<Expr>? Decode(Expr result, Dictionary<string, Expr> env,
                                         int maxSteps = Reducer.DefaultMaxSteps)
        {
            if (!env.TryGetValue("ISNIL", out var isNilDef) ||
                !env.TryGetValue("HEAD",  out var headDef)  ||
                !env.TryGetValue("TAIL",  out var tailDef))
                return null;

            var items = new List<Expr>();
            var current = result;
            for (int i = 0; i <= MaxLen; i++)
            {
                try
                {
                    var isNilResult = Reducer.Reduce(new App(isNilDef, current), env, null, maxSteps);
                    var isNil = ChurchBool.Decode(isNilResult, env, maxSteps);
                    if (isNil is true)  return items;
                    if (isNil is not false) return null;
                    if (i == MaxLen) return null;
                    items.Add(Reducer.Reduce(new App(headDef, current), env, null, maxSteps));
                    current = Reducer.Reduce(new App(tailDef, current), env, null, maxSteps);
                }
                catch { return null; }
            }
            return null;
        }
    }

    // ── Colored console writer ────────────────────────────────────────────────

    /// <summary>A TextWriter that prints each line to the console in a given color.</summary>
    sealed class ColorWriter : TextWriter
    {
        private readonly ConsoleColor _color;
        public ColorWriter(ConsoleColor color) => _color = color;
        public override System.Text.Encoding Encoding => Console.Out.Encoding;

        public override void WriteLine(string? value)
        {
            Console.ForegroundColor = _color;
            Console.WriteLine(value);
            Console.ResetColor();
        }

        public override void Write(string? value)
        {
            Console.ForegroundColor = _color;
            Console.Write(value);
            Console.ResetColor();
        }
    }

    // ── REPL tab-completion ───────────────────────────────────────────────────

    /// <summary>
    /// Provides tab-completion for the ReadLine REPL:
    ///   - All user-defined names from the environment
    ///   - Built-in combinators S K I B C W Y
    ///   - REPL commands  :load :save :env :def :undef :expand :nat :bool :list
    ///                    :bench :trace :set :reset :reload :help
    /// </summary>
    sealed class SkiAutoComplete : IAutoCompleteHandler
    {
        private static readonly string[] Commands =
        [
            ":load", ":save", ":env", ":def", ":undef", ":expand",
            ":nat", ":bool", ":list", ":bench", ":trace",
            ":set", ":reset", ":reload", ":help", ":?",
            "exit"
        ];

        private static readonly string[] Builtins = ["S", "K", "I", "B", "C", "W", "Y"];

        private readonly Func<Dictionary<string, Expr>> _getEnv;

        public SkiAutoComplete(Func<Dictionary<string, Expr>> getEnv) => _getEnv = getEnv;

        public char[] Separators { get; set; } = [' ', '(', ')'];

        public string[] GetSuggestions(string text, int index)
        {
            // Find the word being completed (token starting at 'index')
            var word = text[index..];
            var env = _getEnv();

            IEnumerable<string> candidates = word.StartsWith(':')
                ? Commands
                : Builtins.Concat(env.Keys);

            return candidates
                .Where(c => c.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c)
                .ToArray();
        }
    }
}
