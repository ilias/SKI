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

if (args.Length > 0)
{
    RunLine(string.Concat(args), env);
}
else
{
    Console.WriteLine("SKI(BCWY) Combinator Interpreter");
    Console.WriteLine("Syntax : (expr expr) for application");
    Console.WriteLine("Atoms  : S  K  I  B  C  W  Y  <Name>");
    Console.WriteLine("Define : Name = expr");
    Console.WriteLine("Rules  : I x=x  K x y=x  S x y z=(xz)(yz)");
    Console.WriteLine("         B x y z=x(yz)  C x y z=xzy  W x y=xyy  Y f=f(Yf)");
    Console.WriteLine("Type 'exit' to quit.\n");
    while (true)
    {
        Console.Write("> ");
        var line = Console.ReadLine();
        if (line is null || line.Equals("exit", StringComparison.OrdinalIgnoreCase))
            break;
        if (string.IsNullOrWhiteSpace(line))
            continue;
        RunLine(line.Trim(), env);
    }
}

static void RunLine(string input, Dictionary<string, Expr> env)
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
                env[namePart] = body;
                Console.WriteLine($"  Defined {namePart} = {body}");
                return;
            }
        }

        // Otherwise it's an expression to reduce.
        var tokens = Lexer.Tokenize(input);
        var expr = Parser.Parse(tokens, env);
        if (tokens.Count > 0)
            throw new FormatException("Unexpected tokens after expression.");
        Console.WriteLine($"  Parsed : {expr}");
        var expanded = Expander.Expand(expr, env);
        var result = Reducer.Reduce(expanded);
        Console.WriteLine($"  Result : {result}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Error  : {ex.Message}");
    }
}

static bool IsIdent(string s) =>
    s.Length > 0 && char.IsLetter(s[0]) &&
    s.All(c => char.IsLetterOrDigit(c) || c == '_');

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
            var fun = Parse(tokens, env);
            var arg = Parse(tokens, env);

            if (tokens.Count == 0 || tokens.Peek().Kind != TokenKind.RParen)
                throw new FormatException("Expected ')' to close application.");
            tokens.Dequeue();

            return new App(fun, arg);
        }
    }

    // ── Expander ──────────────────────────────────────────────────────────────

    static class Expander
    {
        /// <summary>Recursively substitutes all NameRef nodes with their stored definitions.</summary>
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
    }

    // ── Reducer ───────────────────────────────────────────────────────────────

    static class Reducer
    {
        private const int MaxSteps = 10_000;

        /// <summary>Fully reduces an expression using normal-order (outermost-leftmost) reduction.</summary>
        public static Expr Reduce(Expr expr)
        {
            for (int step = 0; step < MaxSteps; step++)
            {
                var next = Step(expr);
                if (next is null) return expr;   // normal form
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
        private static Expr? Step(Expr root)
        {
            var path = new Stack<(bool inFun, Expr other)>(32);
            var focus = root;

            while (true)
            {
                if (TryRedex(focus, out var reduced))
                    return Rebuild(reduced, path);

                if (focus is App(var f, var x))
                {
                    path.Push((true, x));   // go into function position
                    focus = f;
                    continue;
                }

                // Leaf (Combinator) — backtrack up the path
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

        private static bool TryRedex(Expr expr, out Expr result)
        {
            // I x  →  x
            if (expr is App(Combinator { Name: 'I' }, var x))
            { result = x; return true; }

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
}

