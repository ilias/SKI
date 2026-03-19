// SKI(BCWY) Combinator Calculus Interpreter
// Grammar (prefix/bracket notation):
//   expr = 'S' | 'K' | 'I' | 'B' | 'C' | 'W' | 'Y' | '(' expr expr ')'
// Example input: ((S K) K)   →  I
//
// Reduction rules:
//   I x       →  x
//   K x y     →  x
//   S x y z   →  (x z)(y z)
//   B x y z   →  x (y z)          (composition;  SKI: S(KS)K)
//   C x y z   →  x z y            (flip;          SKI: S(S(K(S(KS)K))S)(KK))
//   W x y     →  x y y            (duplication;   SKI: SS(KI))
//   Y f       →  f (Y f)          (fixed-point;   SKI: S(K(SII))(S(S(KS)K)(K(SII))))
//
// Run with: dotnet run -- "(((S K) K) I)"
// Or interactively with no arguments.

using SKI;

if (args.Length > 0)
{
    RunExpression(string.Concat(args));
}
else
{
    Console.WriteLine("SKI(BCWY) Combinator Interpreter");
    Console.WriteLine("Syntax : (expr expr) for application");
    Console.WriteLine("Atoms  : S  K  I  B  C  W  Y");
    Console.WriteLine("Rules  : I x=x  K x y=x  S x y z=(xz)(yz)");
    Console.WriteLine("         B x y z=x(yz)  C x y z=xzy  W x y=xyy  Y f=f(Y f)");
    Console.WriteLine("Type 'exit' to quit.\n");
    while (true)
    {
        Console.Write("> ");
        var line = Console.ReadLine();
        if (line is null || line.Equals("exit", StringComparison.OrdinalIgnoreCase))
            break;
        if (string.IsNullOrWhiteSpace(line))
            continue;
        RunExpression(line.Trim());
    }
}

static void RunExpression(string input)
{
    try
    {
        var tokens = Lexer.Tokenize(input);
        var expr = Parser.Parse(tokens);
        Console.WriteLine($"  Parsed : {expr}");
        var result = Reducer.Reduce(expr);
        Console.WriteLine($"  Result : {result}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Error  : {ex.Message}");
    }
}

namespace SKI
{
    // ── Expressions ──────────────────────────────────────────────────────────

    abstract record Expr
    {
        public sealed override string ToString() => Print(this);

        private static string Print(Expr e) => e switch
        {
            Combinator c => c.Name.ToString(),
            App(var f, var x) => $"({Print(f)} {Print(x)})",
            _ => "?"
        };
    }

    record Combinator(char Name) : Expr;
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
    }

    // ── Lexer ─────────────────────────────────────────────────────────────────

    static class Lexer
    {
        public static Queue<char> Tokenize(string source)
        {
            var q = new Queue<char>();
            foreach (var ch in source)
            {
                if (char.IsWhiteSpace(ch)) continue;
                if (ch is 'S' or 'K' or 'I' or 'B' or 'C' or 'W' or 'Y' or '(' or ')')
                    q.Enqueue(ch);
                else
                    throw new FormatException($"Unexpected character '{ch}'.");
            }
            return q;
        }
    }

    // ── Parser ────────────────────────────────────────────────────────────────

    static class Parser
    {
        public static Expr Parse(Queue<char> tokens)
        {
            if (tokens.Count == 0)
                throw new FormatException("Unexpected end of input.");

            var tok = tokens.Dequeue();

            return tok switch
            {
                'S' => Combinators.S,
                'K' => Combinators.K,
                'I' => Combinators.I,
                'B' => Combinators.B,
                'C' => Combinators.C,
                'W' => Combinators.W,
                'Y' => Combinators.Y,
                '(' => ParseApp(tokens),
                ')' => throw new FormatException("Unexpected ')'.")
                ,
                _ => throw new FormatException($"Unexpected token '{tok}'.")
            };
        }

        private static Expr ParseApp(Queue<char> tokens)
        {
            var fun = Parse(tokens);
            var arg = Parse(tokens);

            if (tokens.Count == 0 || tokens.Dequeue() != ')')
                throw new FormatException("Expected ')' to close application.");

            return new App(fun, arg);
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

