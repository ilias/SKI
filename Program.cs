// SKI(BCWY) Combinator Calculus Interpreter
// Grammar:
//   line = Name '=' expr      (definition)
//        | expr               (reduction)
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
// Run with: dotnet run -- "(((S K) K) I)"
// Or interactively with no arguments.

using SKI;

var env = new Dictionary<string, Expr>(StringComparer.Ordinal);
bool traceMode = false;

// Auto-load init.ski from the same directory as the executable (or cwd).
var initPath = FindInitFile();
if (initPath is not null)
    LoadFile(initPath, env, silent: true);

if (args.Length > 0)
{
    RunLine(string.Concat(args), env, traceMode);
}
else
{
    PrintHelp(initPath);
    while (true)
    {
        Console.Write("> ");
        var line = Console.ReadLine();
        if (line is null || line.Equals("exit", StringComparison.OrdinalIgnoreCase))
            break;
        if (string.IsNullOrWhiteSpace(line))
            continue;
        var trimmed = line.Trim();

        // :load <path>
        if (trimmed.StartsWith(":load", StringComparison.OrdinalIgnoreCase))
        {
            var path = trimmed[5..].Trim();
            if (path.Length == 0)
                Console.WriteLine("  Usage: :load <file.ski>");
            else
                LoadFile(path, env, silent: false);
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
                LoadFile(initPath, env, silent: true);
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
                NatLine(rest, env);
            continue;
        }

        // :help
        if (trimmed.Equals(":help", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals(":?", StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp(initPath);
            continue;
        }

        RunLine(trimmed, env, traceMode);
    }
}

static void PrintHelp(string? initPath)
{
    Console.WriteLine("SKI(BCWY) Combinator Interpreter");
    Console.WriteLine("Syntax : (f a b c) = left-assoc application  |  (f a) = binary");
    Console.WriteLine("Atoms  : S  K  I  B  C  W  Y  <Name>");
    Console.WriteLine("Define : Name = expr");
    Console.WriteLine("Rules  : I x=x  K x y=x  S x y z=(xz)(yz)");
    Console.WriteLine("         B x y z=x(yz)  C x y z=xzy  W x y=xyy  Y f=f(Yf)");
    Console.WriteLine("Commands:");
    Console.WriteLine("  :load <file>    load definitions/expressions from a .ski file");
    Console.WriteLine("  :env [pat]      list defined names (optionally filtered)");
    Console.WriteLine("  :expand <expr>  show fully-expanded SKI tree (no reduction)");
    Console.WriteLine("  :nat <expr>     reduce and decode result as a Church numeral");
    Console.WriteLine("  :trace          toggle step-by-step reduction trace");
    Console.WriteLine("  :reset          clear user definitions and reload init.ski");
    Console.WriteLine("  :help           show this message");
    Console.WriteLine("  exit            quit");
    if (initPath is not null)
        Console.WriteLine($"Loaded : {initPath}");
    Console.WriteLine();
}

static void RunLine(string input, Dictionary<string, Expr> env, bool trace)
{
    try
    {
        // Detect   Name = expr   — left side must be a plain identifier, not a builtin.
        var eqIdx = input.IndexOf('=');
        if (eqIdx > 0)
        {
            var namePart = input[..eqIdx].Trim();
            if (IsIdent(namePart))
            {
                if (Combinators.IsBuiltin(namePart))
                    throw new InvalidOperationException($"Cannot redefine built-in combinator '{namePart}'.");
                var bodyPart = input[(eqIdx + 1)..].Trim();
                var bodyTokens = Lexer.Tokenize(bodyPart);
                var body = Parser.Parse(bodyTokens, env);
                if (bodyTokens.Count > 0)
                    throw new FormatException("Unexpected tokens after definition body.");
                // Warn if the name directly references itself in its own body.
                if (Expander.ContainsName(body, namePart))
                    Cwl($"  Warning: '{namePart}' references itself — use Y for recursion", ConsoleColor.Yellow);
                env[namePart] = body;
                Cwl($"  Defined {namePart} = {body}", ConsoleColor.DarkGreen);
                return;
            }
        }

        // Otherwise it's an expression to reduce.
        var tokens = Lexer.Tokenize(input);
        var expr = Parser.Parse(tokens, env);
        if (tokens.Count > 0)
            throw new FormatException("Unexpected tokens after expression.");
        Cwl($"  Parsed : {expr}", ConsoleColor.DarkGray);
        var result = Reducer.Reduce(expr, env, trace ? new ColorWriter(ConsoleColor.DarkGray) : null);
        Cwl($"  Result : {result}", ConsoleColor.Cyan);
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

static void NatLine(string input, Dictionary<string, Expr> env)
{
    try
    {
        var tokens = Lexer.Tokenize(input);
        var expr = Parser.Parse(tokens, env);
        if (tokens.Count > 0)
            throw new FormatException("Unexpected tokens after expression.");
        var result = Reducer.Reduce(expr, env, null);
        Cwl($"  Result : {result}", ConsoleColor.DarkGray);
        var n = ChurchNat.Decode(result, env);
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

static bool IsIdent(string s) =>
    s.Length > 0 && char.IsLetter(s[0]) &&
    s.All(c => char.IsLetterOrDigit(c) || c == '_');

static void Cwl(string text, ConsoleColor color)
{
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ResetColor();
}

/// <summary>Loads and executes every non-blank, non-comment line in a .ski file.</summary>
static void LoadFile(string path, Dictionary<string, Expr> env, bool silent)
{
    if (!File.Exists(path))
    {
        Cwl($"  Error  : File not found: '{path}'", ConsoleColor.Red);
        return;
    }
    int defined = 0, errors = 0;
    foreach (var raw in File.ReadLines(path))
    {
        // Strip inline comments and trim whitespace
        var commentIdx = raw.IndexOf('#');
        var line = (commentIdx >= 0 ? raw[..commentIdx] : raw).Trim();
        if (line.Length == 0) continue;
        try
        {
            // Only definitions are expected in library files.
            var eqIdx = line.IndexOf('=');
            if (eqIdx > 0)
            {
                var namePart = line[..eqIdx].Trim();
                if (IsIdent(namePart))
                {
                    if (Combinators.IsBuiltin(namePart))
                        throw new InvalidOperationException($"Cannot redefine built-in combinator '{namePart}'.");
                    var bodyPart = line[(eqIdx + 1)..].Trim();
                    var bodyTokens = Lexer.Tokenize(bodyPart);
                    var body = Parser.Parse(bodyTokens, env);
                    if (bodyTokens.Count > 0)
                        throw new FormatException("Unexpected tokens after definition body.");
                    env[namePart] = body;
                    if (!silent) Cwl($"  Defined {namePart} = {body}", ConsoleColor.DarkGreen);
                    defined++;
                    continue;
                }
            }
            // Non-definition lines in a file: evaluate and print.
            RunLine(line, env, false);
        }
        catch (Exception ex)
        {
            Cwl($"  Error  : {path}: {ex.Message}", ConsoleColor.Red);
            errors++;
        }
    }
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

    enum TokenKind { LParen, RParen, Ident }
    record Token(TokenKind Kind, string Value);

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
                if (ch == '(') { q.Enqueue(new Token(TokenKind.LParen, "(")); i++; continue; }
                if (ch == ')') { q.Enqueue(new Token(TokenKind.RParen, ")")); i++; continue; }
                if (char.IsLetter(ch) || ch == '_')
                {
                    int start = i;
                    while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                        i++;
                    q.Enqueue(new Token(TokenKind.Ident, source[start..i]));
                    continue;
                }
                throw new FormatException($"Unexpected character '{ch}'.");
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
                TokenKind.LParen  => ParseApp(tokens, env),
                TokenKind.RParen  => throw new FormatException("Unexpected ')'.")
                ,
                _ => throw new FormatException($"Unexpected token '{tok.Value}'.")
            };
        }

        private static Expr ParseApp(Queue<Token> tokens, Dictionary<string, Expr> env)
        {
            // Parse the function and at least one argument, then keep consuming
            // additional arguments until ')' — left-associative: (f a b c) = (((f a) b) c).
            var result = Parse(tokens, env);

            if (tokens.Count == 0 || tokens.Peek().Kind == TokenKind.RParen)
                throw new FormatException("Application requires at least one argument.");

            while (tokens.Count > 0 && tokens.Peek().Kind != TokenKind.RParen)
                result = new App(result, Parse(tokens, env));

            if (tokens.Count == 0)
                throw new FormatException("Expected ')' to close application.");
            tokens.Dequeue(); // consume ')'

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

    // ── Reducer ───────────────────────────────────────────────────────────────

    static class Reducer
    {
        private const int MaxSteps = 1_000_000;

        /// <summary>Fully reduces an expression using normal-order (outermost-leftmost) reduction.
        /// Name references are resolved lazily during reduction — no pre-expansion needed.
        /// <paramref name="trace"/> receives each intermediate expression if non-null.</summary>
        public static Expr Reduce(Expr expr, Dictionary<string, Expr> env, TextWriter? trace)
        {
            int step = 0;
            for (; step < MaxSteps; step++)
            {
                var next = Step(expr, env);
                if (next is null) return expr;   // normal form
                if (trace is not null)
                    trace.WriteLine($"  [{step + 1,6}] {next}");
                expr = next;
            }
            throw new InvalidOperationException($"Reduction did not terminate after {MaxSteps} steps.");
        }

        /// <summary>
        /// One outermost-leftmost reduction step using an explicit path (zipper).
        /// Returns null when the expression is already in normal form.
        ///
        /// Stack entries encode the path from root to focus:
        ///   (inFun=true,  other=arg) — descended into function position; arg is the sibling
        ///   (inFun=false, other=fun) — descended into argument position; fun is the sibling
        /// </summary>
        private static Expr? Step(Expr root, Dictionary<string, Expr> env)
        {
            var path = new Stack<(bool inFun, Expr other)>(32);
            var focus = root;

            while (true)
            {
                if (TryRedex(focus, env, out var reduced))
                    return Rebuild(reduced, path);

                if (focus is App(var f, var x))
                {
                    path.Push((true, x));   // go into function position
                    focus = f;
                    continue;
                }

                // Leaf (Combinator or unresolvable NameRef) — backtrack up the path
                while (true)
                {
                    if (path.Count == 0) return null;       // whole tree is normal form

                    var (inFun, other) = path.Pop();
                    if (inFun)
                    {
                        // Finished function sub-tree; now explore argument
                        path.Push((false, focus));
                        focus = other;
                        break;                              // continue outer loop
                    }
                    // Finished argument sub-tree; reconstruct and go up
                    focus = new App(other, focus);
                }
            }
        }

        private static Expr Rebuild(Expr node, Stack<(bool inFun, Expr other)> path)
        {
            while (path.Count > 0)
            {
                var (inFun, other) = path.Pop();
                node = inFun
                    ? new App(node, other)   // node is fun, other is arg
                    : new App(other, node);  // other is fun, node is arg
            }
            return node;
        }

        private static bool TryRedex(Expr expr, Dictionary<string, Expr> env, out Expr result)
        {
            // NameRef — resolve lazily from the environment
            if (expr is NameRef(var refName))
            {
                if (env.TryGetValue(refName, out var def))
                { result = def; return true; }
                // Undefined name stays as-is (will surface as a stuck term)
                result = default!;
                return false;
            }

            // I x  →  x
            if (expr is App(Combinator { Name: 'I' }, var x))
            { result = x; return true; }

            // I applied lazily: if the function position holds a NameRef that resolves to I
            // (handled by the NameRef case above — the ref is replaced first, then re-evaluated)

            // K x y  →  x
            if (expr is App(App(Combinator { Name: 'K' }, var kx), _))
            { result = kx; return true; }

            // S x y z  →  (x z)(y z)
            if (expr is App(App(App(Combinator { Name: 'S' }, var sx), var sy), var sz))
            { result = new App(new App(sx, sz), new App(sy, sz)); return true; }

            // B x y z  →  x (y z)
            if (expr is App(App(App(Combinator { Name: 'B' }, var bx), var by), var bz))
            { result = new App(bx, new App(by, bz)); return true; }

            // C x y z  →  x z y
            if (expr is App(App(App(Combinator { Name: 'C' }, var cx), var cy), var cz))
            { result = new App(new App(cx, cz), cy); return true; }

            // W x y  →  x y y
            if (expr is App(App(Combinator { Name: 'W' }, var wx), var wy))
            { result = new App(new App(wx, wy), wy); return true; }

            // (Y f) x  →  f (Y f) x     [lazy: only unfolds when applied to an argument]
            // "Y f" alone is stuck (weak-head normal form), preventing runaway tree growth.
            if (expr is App(App(Combinator { Name: 'Y' }, var yf), var yx))
            { result = new App(new App(yf, new App(Combinators.Y, yf)), yx); return true; }

            result = default!;
            return false;
        }
    }

    // ── Church-numeral decoder ────────────────────────────────────────────────

    static class ChurchNat
    {
        private const int MaxN = 10_000;

        /// <summary>
        /// Decodes a fully-reduced Church numeral by applying it to a counter function.
        /// Strategy: reduce (n SUCC_MARKER ZERO_MARKER) step by step, counting I-like
        /// unfolding — or simply apply n to a fresh sentinel and count applications.
        ///
        /// Practical approach: reduce (expr f z) where f = a fresh "tick" name that
        /// increments a counter, using a lightweight direct evaluation.
        /// We use the algebraic identity: a normal-form Church numeral n applied to
        /// SUCC and ZERO should reduce to the n-th numeral literal.  Instead we
        /// evaluate n applied to a sentinel successor atom and a sentinel zero atom
        /// and count how many times the successor appears in the spine.
        /// </summary>
        public static int? Decode(Expr result, Dictionary<string, Expr> env)
        {
            // Strategy: apply the result to a fresh sentinel successor S' and a fresh
            // sentinel zero Z', reduce, then count the depth of the S'-application
            // spine in the output.
            //
            // We use single-character sentinel atoms that cannot appear in any real
            // SKI expression (they're outside A-Z and not valid identifiers), stored
            // as Combinator nodes with custom names.
            var tick = new Combinator('\u0001');   // successor sentinel (non-printable)
            var zero = new Combinator('\u0002');   // zero sentinel

            // Build ((result tick) zero) and reduce it.
            Expr applied = new App(new App(result, tick), zero);

            // Use a local env copy that maps the sentinels if needed (they have no
            // reduction rules so they stay as atoms — exactly what we want).
            Expr reduced;
            try
            {
                reduced = Reducer.Reduce(applied, env, null);
            }
            catch
            {
                return null;
            }

            // Now count how many times `tick` wraps around `zero` in the result.
            // A Church n applied to tick and zero produces:
            //   n=0: zero
            //   n=1: (tick zero)
            //   n=2: (tick (tick zero))
            //   n=k: (tick^k zero)
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
}
