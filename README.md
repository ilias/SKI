# SKI(BCWY) Combinator Calculus Interpreter

A complete combinator calculus interpreter written in C# (.NET 10). Supports the full **SKI + BCWY** basis, a named-definition system, a standard library (`init.ski`), and an interactive REPL.

---

## Table of Contents

- [SKI(BCWY) Combinator Calculus Interpreter](#skibcwy-combinator-calculus-interpreter)
  - [Table of Contents](#table-of-contents)
  - [Getting Started](#getting-started)
  - [Expression Syntax](#expression-syntax)
  - [Built-in Combinators](#built-in-combinators)
  - [Reduction Strategy](#reduction-strategy)
  - [REPL Commands](#repl-commands)
  - [Name Definitions](#name-definitions)
  - [`.ski` Library Files](#ski-library-files)
  - [Standard Library — `init.ski`](#standard-library--initski)
    - [Primitive Aliases](#primitive-aliases)
    - [Booleans](#booleans)
    - [Church Pairs](#church-pairs)
    - [Church Numerals](#church-numerals)
      - [Arithmetic](#arithmetic)
      - [Predicates \& Comparisons](#predicates--comparisons)
    - [Church Lists](#church-lists)
    - [Higher-Order Utilities](#higher-order-utilities)
  - [Encoding Conventions](#encoding-conventions)
    - [Reading boolean results](#reading-boolean-results)
    - [Reading numeral results](#reading-numeral-results)
    - [Recursive definitions with Y](#recursive-definitions-with-y)
  - [Limitations](#limitations)

---

## Getting Started

```
dotnet run
```

Starts the interactive REPL. Pass an expression directly on the command line for one-shot evaluation:

```
dotnet run -- "(((S K) K) I)"
```

`init.ski` is loaded automatically at startup from the same directory as the executable, or the current working directory if not found there.

---

## Expression Syntax

A parenthesised form `(f a b c ...)` applies `f` to all arguments **left-associatively**:

| Written as | Equivalent to |
|---|---|
| `(f a)` | `(f a)` |
| `(f a b)` | `((f a) b)` |
| `(f a b c)` | `(((f a) b) c)` |

The old fully-explicit nesting `((f a) b)` still works identically — the two forms are interchangeable.

**Atoms** are either a single uppercase letter (`S K I B C W Y`) or a user-defined name (`[A-Za-z][A-Za-z0-9_]*`).

**Grammar:**

```
line  = Name '=' expr            ;  definition
      | expr                     ;  reduction
expr  = ATOM
      | '(' expr expr+ ')'      ;  left-associative application
```

Comments start with `#` and run to the end of the line.

---

## Built-in Combinators

| Combinator | Rule | Description |
|---|---|---|
| `I` | `I x → x` | Identity |
| `K` | `K x y → x` | Constant (first projection) |
| `S` | `S x y z → (x z)(y z)` | Substitution / S-combinator |
| `B` | `B x y z → x (y z)` | Composition |
| `C` | `C x y z → x z y` | Flip (argument order swap) |
| `W` | `W x y → x y y` | Duplication |
| `Y` | `(Y f) x → f (Y f) x` | Fixed-point (lazy) |

> **Y is lazy**: `Y f` alone is a normal form. It only unfolds when applied to an argument, preventing infinite expansion.

Built-in names (`S K I B C W Y`) cannot be redefined by user code.

---

## Reduction Strategy

- **Outermost-leftmost** (normal-order) reduction — guarantees finding a normal form whenever one exists.
- Fully **iterative**: an internal zipper stack replaces recursion, so there is no risk of stack overflow on deeply nested expressions.
- Reduction stops after **10,000 steps**; expressions that exceed this limit are returned as-is with a step-limit message.

---

## REPL Commands

| Input | Effect |
|---|---|
| `(expr)` | Evaluate and print the reduced form |
| `Name = expr` | Define `Name` and add it to the environment |
| `:load <file.ski>` | Load definitions (and any bare expressions) from a file |
| `:env` | List all defined names alphabetically with their bodies |
| `:env <pattern>` | List definitions whose name contains `pattern` (case-insensitive) |
| `exit` | Quit the REPL |

**Example session:**

```
> TRUE = K
  Defined TRUE = K
> FALSE = (K I)
  Defined FALSE = (K I)
> (((TRUE S) K) I)
  Parsed : (((TRUE S) K) I)
  Result : S
> :env bool
  AND    = ((S S) (K FALSE))
  FALSE  = (K I)
  IF     = I
  ...
```

---

## Name Definitions

A definition has the form:

```
Name = expr
```

- `Name` must match `[A-Za-z][A-Za-z0-9_]*` and must not be a built-in combinator.
- The body `expr` may reference any previously defined name (or even the name being defined, enabling mutual recursion via `Y`).
- Names are **expanded lazily** at reduction time — the stored body is substituted just before reduction.
- **Cycle detection**: defining `A = A` (direct or indirect) is detected and raises an error at expansion time.

---

## `.ski` Library Files

A `.ski` file is a plain text file containing any mix of:

- **Comment lines**: `# ...`
- **Inline comments**: `Name = expr  # comment`
- **Definition lines**: `Name = expr`
- **Bare expression lines**: `(expr)` — evaluated immediately when the file is loaded; useful for tests and sanity checks.

Load a file from the REPL:

```
:load mylib.ski
```

The interpreter prints each defined name as it processes the file, then reports the total count of definitions and any errors.

---

## Standard Library — `init.ski`

Loaded automatically at startup. All 53 definitions use strictly binary `(f x)` notation.

### Primitive Aliases

| Name | Definition | Description |
|---|---|---|
| `ID` | `I` | Identity function |
| `CONST` | `K` | Constant function |
| `COMPOSE` | `B` | Function composition: `COMPOSE f g x = f (g x)` |
| `FLIP` | `C` | Argument flip: `FLIP f x y = f y x` |
| `DUP` | `W` | Duplicate argument: `DUP f x = f x x` |
| `FIX` | `Y` | Fixed-point combinator (lazy) |

---

### Booleans

Church booleans encode a boolean as a binary selector: `TRUE x y = x`, `FALSE x y = y`.

**Probe a boolean result** by applying it to two sentinel values:
`(((b S) K) I)` → `S` means TRUE, `K` means FALSE.

| Name | Description |
|---|---|
| `TRUE` | `K` — selects first argument |
| `FALSE` | `(K I)` — selects second argument |
| `NOT` | `C` — `NOT b x y = b y x` |
| `AND` | `AND p q = p q FALSE` |
| `OR` | `OR p q = p TRUE q` |
| `XOR` | `XOR p q = OR (AND p (NOT q)) (AND (NOT p) q)` |
| `NAND` | `NOT (AND p q)` |
| `NOR` | `NOT (OR p q)` |
| `IF` | `I` — `IF` is redundant since booleans are already selectors |

**Examples:**

```
(((NOT TRUE) K) S)           # → S  (NOT TRUE = FALSE, selects second)
(((((AND TRUE) FALSE) K) S) I)  # → K  (FALSE)
(((((OR FALSE) TRUE) S) K) I)   # → S  (TRUE)
```

---

### Church Pairs

A pair `PAIR a b` stores two values; `FST` and `SND` project them.

| Name | Description |
|---|---|
| `PAIR` | `PAIR a b f = f a b` — constructs a pair |
| `FST` | `FST p = p TRUE` — first projection |
| `SND` | `SND p = p FALSE` — second projection |
| `SWAP` | `SWAP p = PAIR (SND p) (FST p)` — swap pair elements |

**Examples:**

```
((FST ((PAIR S) K)) I)       # → S
((SND ((PAIR S) K)) I)       # → K
((FST (SWAP ((PAIR S) K))) I)  # → K
```

---

### Church Numerals

A Church numeral `n` represents the natural number $n$ as `n f x = f^n x` — apply `f` exactly `n` times to `x`.

**Named constants:** `ZERO ONE TWO THREE FOUR FIVE SIX SEVEN EIGHT NINE TEN`

**Test numeral equality** with: `(((EQ n m) S) K)` → `S` if `n = m`.

#### Arithmetic

| Name | Description | Example |
|---|---|---|
| `SUCC` | Successor: `SUCC n = n+1` | `(SUCC TWO) = THREE` |
| `PRED` | Predecessor (clamped at 0): `PRED n = max(n-1, 0)` | `(PRED THREE) = TWO`, `(PRED ZERO) = ZERO` |
| `ADD` | Addition: `ADD m n = m+n` | `((ADD TWO) THREE) = FIVE` |
| `MUL` | Multiplication: `MUL m n = m*n` (= `B`) | `((MUL TWO) THREE) = SIX` |
| `POW` | Exponentiation: `POW m n = m^n` (= `C I`) | `((POW TWO) THREE) = EIGHT` |
| `SUB` | Subtraction (clamped at 0): `SUB m n = max(m-n, 0)` | `((SUB THREE) ONE) = TWO`, `((SUB ONE) THREE) = ZERO` |

#### Predicates & Comparisons

| Name | Description |
|---|---|
| `ISZERO` | `ISZERO n` → `TRUE` if `n = 0`, else `FALSE` |
| `LEQ` | `LEQ m n` → `TRUE` if `m ≤ n` |
| `EQ` | `EQ m n` → `TRUE` if `m = n` |
| `GEQ` | `GEQ m n` → `TRUE` if `m ≥ n` |
| `GT` | `GT m n` → `TRUE` if `m > n` |
| `LT` | `LT m n` → `TRUE` if `m < n` |

**Examples:**

```
(((ISZERO ZERO) S) K)              # → S  (TRUE)
(((ISZERO TWO) K) S)               # → S  (FALSE)
((((EQ ((ADD TWO) THREE)) FIVE) S) K)   # → S  (TRUE)
((((EQ ((MUL TWO) THREE)) SIX) S) K)   # → S  (TRUE)
((((EQ ((POW TWO) THREE)) EIGHT) S) K) # → S  (TRUE)
((((LEQ TWO) THREE) S) K)          # → S  (TRUE)
((((GT THREE) TWO) S) K)           # → S  (TRUE)
```

---

### Church Lists

Lists are encoded with a Church-style cons structure.  
`NIL` is a sentinel; `ISNIL` tests for it; `CONS` prepends an element; `HEAD`/`TAIL` deconstruct.

| Name | Description |
|---|---|
| `NIL` | Empty list (= `TRUE`) |
| `ISNIL` | `ISNIL l` → `TRUE` if `l` is `NIL` |
| `CONS` | `CONS h t` — prepend `h` to list `t` |
| `HEAD` | `HEAD l` — first element |
| `TAIL` | `TAIL l` — rest of the list |

**Examples:**

```
(((ISNIL NIL) S) K)                          # → S  (TRUE)
(((ISNIL ((CONS S) NIL)) K) S)               # → S  (FALSE)
((HEAD ((CONS S) NIL)) I)                    # → S
(((ISNIL (TAIL ((CONS S) NIL))) S) K)        # → S  (tail of singleton = NIL)
((HEAD (TAIL ((CONS S) ((CONS K) NIL)))) I)  # → K  (second element)
```

---

### Higher-Order Utilities

| Name | Description |
|---|---|
| `APPLY` | `APPLY f x = f x` — identity on functions (= `I`) |
| `TWICE` | `TWICE f x = f (f x)` — apply `f` twice |
| `THRICE` | `THRICE f x = f (f (f x))` — apply `f` three times |
| `ON` | `ON g f x y = g (f x) (f y)` — Psi combinator; lifts `g` through `f` |
| `ITERATE` | `ITERATE n f x = f^n x` — same as Church numeral application (= `I`, alias for readability) |

**Examples:**

```
((((EQ ((TWICE SUCC) ONE)) THREE) S) K)     # → S  (1+1+1 = 3)
((((EQ ((THRICE SUCC) ZERO)) THREE) S) K)   # → S  (0+1+1+1 = 3)
# ON ADD FST: add the first elements of two pairs
((((EQ ((((ON ADD) FST) ((PAIR TWO) THREE)) ((PAIR ONE) FOUR))) THREE) S) K)  # → S
```

---

## Encoding Conventions

### Reading boolean results

Since `TRUE = K` and `FALSE = (K I)`, you can "decode" a boolean `b` by applying it to two distinct normal-form atoms:

```
(((b S) K) I)   ;  → S  means TRUE
                ;  → K  means FALSE
```

### Reading numeral results

Church numeral `n` is opaque as a combinator expression. Use `EQ` to compare:

```
((((EQ n m) S) K) I)   ;  → S  means n = m
                        ;  → K  means n ≠ m
```

Alternatively, apply `n` to `SUCC` and `ZERO` — the result reduces to the equivalent literal numeral (`I` for ONE, `((S B) I)` for TWO, etc.).

### Recursive definitions with Y

To define a recursive function, write a "step" combinator that takes a self-reference as its first argument, then wrap it with `Y`:

```
# Example: define "double" recursively (trivial — normally use MUL TWO)
# DOUBLEF rec n = ISZERO n ZERO (SUCC (SUCC (rec (PRED n))))
# (in practice, use non-recursive MUL TWO directly)
DOUBLEF = ...
DOUBLE  = (Y DOUBLEF)
```

Because `Y` is lazy, `(Y DOUBLEF)` does not unfold until applied to an argument.

---

## Limitations

- **No integers**: only Church numerals. Arithmetic on large numerals is exponentially expensive.
- **Step limit**: reduction halts after 10,000 steps; complex recursive computations may hit this ceiling.
- **No I/O**: the interpreter is purely functional — expressions may only be evaluated, not executed for effects.
- **No type system**: all terms are untyped; ill-formed expressions reduce to irreducible terms rather than raising type errors.
- **No multi-arg shorthand**: `(f a b)` is a syntax error; use `((f a) b)` instead.
