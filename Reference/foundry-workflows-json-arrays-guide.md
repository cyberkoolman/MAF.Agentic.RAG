# Microsoft Foundry Workflows — Strings, JSON, and Arrays in the Visual Designer

A practical reference for handling strings, JSON, and especially arrays in Microsoft Foundry's visual workflow designer (no YAML, no SDK). Compiled from official Microsoft Learn docs, the Microsoft Foundry GitHub discussions, Copilot Studio docs (which Foundry workflows are built on), and Power Fx references.

---

## The Mental Model You Need

Foundry workflows are built on **Copilot Studio patterns** with **Power Fx** as the underlying expression language. There are four data primitives, and getting these right is the whole game:

- **String** — plain text. `System.LastMessage.Text` is the user's last message as a string.
- **Number / Boolean** — straightforward scalars.
- **Record** — a single object with named fields (Power Fx's term for "object"). Equivalent to a JSON `{}`.
- **Table** — a collection of records (Power Fx's term for "array"). Equivalent to a JSON `[]`.

### Variable Scope Prefixes

Every variable reference needs a scope prefix or you'll get "Name isn't valid" errors:

- `System.` — built-in system variables (e.g., `System.LastMessage.Text`, `System.Conversation.Id`, `System.User.Language`)
- `Local.` — workflow-local variables you create

### The Single Biggest Array Gotcha

If you declare an array of scalars like `[1, 2, 3]`, it is stored as a table of objects: `[{Value: 1}, {Value: 2}, {Value: 3}]`. You must reference each item using `.Value`. This trips up almost everyone.

```
// Declaring
Set(Local.MyList, [1, 2, 3])

// Iterating — you need .Value
ForAll(Local.MyList, ThisRecord.Value * 2)
```

Arrays of *records* don't have this issue — only arrays of bare scalars.

---

## The Two Ways to Get Structured JSON Into a Variable

### Method 1 (Best): JSON Schema Output on the Agent Itself

This is the cleanest path and what Microsoft now recommends. When an agent returns JSON via a schema, Foundry parses it automatically — no Parse Value node needed, no string-to-record conversion. **This is the recommended workaround for the current Parse Value array bug** (see below).

**Steps:**

1. In the **Invoke Agent** node → **Details** → click the parameters icon → set **Text format** to **JSON Schema**.
2. Paste your schema. Microsoft's own math example shows the right structure (object at top with arrays nested inside):

```json
{
  "name": "math_response",
  "schema": {
    "type": "object",
    "properties": {
      "steps": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "explanation": {"type": "string"},
            "output": {"type": "string"}
          },
          "required": ["explanation", "output"],
          "additionalProperties": false
        }
      },
      "final_answer": {"type": "string"}
    },
    "additionalProperties": false,
    "required": ["steps", "final_answer"],
    "strict": true
  }
}
```

3. In **Action settings**, choose **Save output json_object/json_schema as** and create a variable (e.g., `Local.MathResult`).
4. You can now reference fields directly in Power Fx: `Local.MathResult.final_answer`, `First(Local.MathResult.steps).explanation`, etc.

**Critical: the top-level `type` MUST be `object`.** If your agent's schema is a bare array at the top level, the End step (and most downstream nodes) will fail with errors like:

> "The requested operation requires an element of type 'Object', but the target element has type 'Array'."

**Always wrap arrays inside an object.**

### Method 2: Parse Value on a String Variable

Use this when the JSON arrives as a string (HTTP response, message text, agent output that isn't schema-controlled).

**Steps:**

1. Add a **Parse Value** node (Variable management → Parse value).
2. Point it at the string variable (e.g., `System.LastMessage.Text` or your saved variable).
3. Data type → **From sample data** → **Get schema from sample JSON** → paste a representative JSON sample → **Confirm**.
4. Save as a new variable.

Foundry infers the Record/Table structure from your sample. **The crucial part: paste a complete, representative sample.** If your sample omits a field, that field won't exist on the parsed variable.

---

## The Array Bug — And the Workaround

There is a real, currently-open bug in Foundry workflows (tracked in [microsoft-foundry Discussion #218](https://github.com/orgs/microsoft-foundry/discussions/218) and [microsoft/agent-framework Issue #4195](https://github.com/microsoft/agent-framework/issues/4195)):

> "When I attempt to use Parse value, all object data is stripped. The top-level array and object notation remain."
>
> "When setting array properties within an object, all parsing fails. When setting purely object/string/int properties, it works as intended."

**The Microsoft-support-confirmed workarounds, in order of preference:**

1. **Use JSON Schema on the agent (Method 1 above).** This sidesteps Parse Value entirely.
2. **If you must use Parse Value, drop into YAML view** (toggle in the top bar) and write the node explicitly with `kind: Table` for arrays:

```yaml
- kind: ParseValue
  id: parse_json
  variable: Local.ParsedJson
  valueType:
    kind: Record
    properties:
      name: String
      city: String
      systems:
        kind: Table
        properties:
          system_name: String
          confidence: Number
  value: =System.LastMessageText
```

Note `kind: Table` for arrays, with `properties` describing each row. Save in YAML, flip back to the visualizer — the node will now show up correctly typed.

---

## Creating Arrays From Scratch With Power Fx

In a **Set variable value** node, switch the value input to formula mode and use the `Table()` function:

```
Table(
  {Name: "Phoenix", Role: "scout"},
  {Name: "Ember", Role: "fixer"},
  {Name: "Forge", Role: "builder"}
)
```

That gives you a real Table (array of records) you can iterate with `ForAll`, filter with `Filter`, index with `Index(table, n)`, or count with `CountRows`.

For a simple list of strings or numbers, the shorthand `["a", "b", "c"]` works — but remember each item becomes `{Value: "a"}`, so you reference it as `ThisRecord.Value` inside a ForAll.

### Appending to an Existing Array

There is no native Append node; use Power Fx:

```
// Mutate in place
Collect(Local.MyTable, {Name: "Sentinel", Role: "guard"})

// Or build a fresh combined table
Table(Local.MyTable, {Name: "Sentinel", Role: "guard"})
```

### Building an Array From a Loop

Use `ForAll` to project one table into another:

```
ForAll(
  Local.RawData,
  {
    id: ThisRecord.id,
    score: ThisRecord.confidence * 100,
    label: Upper(ThisRecord.name)
  }
)
```

---

## Accessing and Manipulating Array Data

Once you have a typed Table, these patterns cover most needs:

| Goal | Power Fx |
|---|---|
| First item | `First(Local.Results.systems)` |
| Last item | `Last(Local.Results.systems)` |
| Nth item (1-indexed) | `Index(Local.Results.systems, 2)` |
| Field on Nth item | `Index(Local.Results.systems, 1).confidence` |
| Filter by condition | `Filter(Local.Results.systems, confidence > 0.8)` |
| Count | `CountRows(Local.Results.systems)` |
| Iterate (in workflow) | Use **For each** node, pointed at `Local.Results.systems` |
| Iterate (in expression) | `ForAll(Local.Results.systems, ThisRecord.name)` |
| Look up one record | `LookUp(Local.Results.systems, system_name = "auth")` |
| Take first N | `FirstN(Local.Results.systems, 3)` |
| Take last N | `LastN(Local.Results.systems, 3)` |
| Check existence | `!IsBlank(LookUp(Local.Results.systems, name = "x"))` |
| Sum a field | `Sum(Local.Results.systems, confidence)` |
| Sort | `Sort(Local.Results.systems, confidence, SortOrder.Descending)` |

### Inside a For Each Node

Inside the **For each** loop, the built-in iterator variable is `ThisRecord`. You can reference its fields directly: `ThisRecord.system_name`, `ThisRecord.confidence`, etc.

Common pattern — **iterate then conditionally dispatch**:

1. **For each** node, points at `Local.RouterResult.systems`
2. Inside it, **If/else** with condition `ThisRecord.confidence > 0.8`
3. Inside the true branch, **Invoke Agent** with inputs derived from `ThisRecord` fields

---

## What Does NOT Work (Don't Waste Time)

A few things the general Power Fx docs imply should work, but **don't** work in current Foundry workflows:

| Thing | Why it fails | Alternative |
|---|---|---|
| `ParseJSON()` function | Explicitly unsupported in workflows. Error: `'ParseJSON' is an unknown or unsupported function` | Use Parse Value node or schema-on-agent |
| `JSON()` function | Unsupported | Use `Text()` or `Concatenate()` to build strings |
| String interpolation `$"Hello {name}"` | Unreliable because the underlying YAML uses similar syntax | Use `Concatenate()` or `&` operator |
| `{Local.X.field}` directly in message text | Gives symbol syntax errors when nested | Wrap in Power Fx: `{Text(Local.X.field)}` |
| Returning a bare array as the workflow End output | Fails type checking — End expects an object | Wrap in object: `{ results: Local.MyArray }` |
| Returning array-of-records to Copilot Studio | Copilot Studio doesn't accept array-of-records as output variable | Wrap in object, or serialize to JSON string |

---

## Literal Value Formats in Power Fx

When typing literals into formula fields:

| Type | Format Examples |
|---|---|
| String | `"hi"`, `"hello world!"`, `"copilot"` |
| Boolean | `true`, `false` (lowercase only) |
| Number | `1`, `532`, `5.258`, `-9201` |
| Record | `{ id: 1 }`, `{ name: "John", info: { age: 25 } }` |
| Table | `[1]`, `[45, 8, 2]`, `["cats", "dogs"]`, `Table({a:1},{a:2})` |
| Date/Time | `Time(5,0,23)`, `Date(2022,5,24)`, `DateTimeValue("May 10, 2022 5:00:00 PM")` |
| Blank | `Blank()` |

---

## Common Power Fx Functions Supported in Workflows

### String

`Text()`, `Concat()`, `Concatenate()` (also `&`), `Len()`, `Lower()`, `Upper()`, `Proper()`, `IsMatch()`, `Match()`, `MatchAll()`, `StartsWith()`, `EndsWith()`, `Find()`, `Replace()`, `Substitute()`

### Boolean / Logic

`And()`, `Or()`, `Not()`, `If()`, `Switch()`, `Boolean()`

### Number

`Decimal()`, `Float()`, `Value()`, `Int()`, `Round()`, `RoundDown()`, `RoundUp()`, `Trunc()`

### Record / Table (Arrays)

`Concat()`, `Concatenate()`, `Count()`, `CountA()`, `CountIf()`, `CountRows()`, `ForAll()`, `First()`, `FirstN()`, `Index()`, `Last()`, `LastN()`, `Filter()`, `Search()`, `LookUp()`, `Sort()`, `Sum()`, `Min()`, `Max()`, `Average()`, `Collect()`, `Table()`

### Date / Time

`Date()`, `DateTime()`, `Time()`, `DateValue()`, `TimeValue()`, `DateTimeValue()`, `Day()`, `Month()`, `Year()`, `Hour()`, `Minute()`, `Second()`, `Weekday()`, `Now()`, `Today()`, `UTCNow()`, `UTCToday()`, `DateAdd()`, `DateDiff()`, `TimeZoneOffset()`

### Blank / Error Handling

`Blank()`, `Coalesce()`, `IsBlank()`, `IsEmpty()`, `Error()`, `IfError()`, `IsError()`, `IsBlankOrError()`

---

## Practical Debugging Tips

- **Save explicitly after every change.** Foundry doesn't autosave. Every save creates a new immutable version.
- **Add a temporary Send Message node to probe values.** Use it with concrete field access:
  ```
  {Concatenate("First system: ", First(Local.Result.systems).name, " | confidence: ", Text(First(Local.Result.systems).confidence))}
  ```
- **Flip on YAML view when stuck.** The YAML surfaces real type mismatches (`kind: Record` vs `kind: Table`) far more clearly than the visualizer.
- **Use the Version dropdown to roll back** when an experiment breaks things.
- **Verify each node's saved output variable** matches the type you expect — agent JSON outputs with broken schemas often silently fail to a string or empty record.

---

## Common Error Messages and Fixes

| Error | Cause | Fix |
|---|---|---|
| `'ParseJSON' is an unknown or unsupported function` | Trying to use Power Apps `ParseJSON()` | Use Parse Value node or schema-on-agent |
| `Name isn't valid` | Missing scope prefix | Add `System.` or `Local.` |
| `Type mismatch` | Variable type doesn't match expected | Use `Text()`, `Value()`, etc. to convert |
| `requires an element of type 'Object', but the target element has type 'Array'` | End step or downstream node got a bare array | Wrap array in an object: `{items: Local.MyArray}` |
| Symbol syntax errors when using `{Local.X.field}` in messages | Direct field access in message text | Wrap in Power Fx: `{Text(Local.X.field)}` |
| Parsed JSON shows empty `{}` for each array item | Foundry Parse Value array bug | Use schema-on-agent instead, or edit YAML directly with `kind: Table` |

---

## A Recommended End-to-End Pattern (Agent Routing by Confidence)

For most real cases — including routing to multiple specialist agents based on a confidence-scored array — this is the path of least pain:

**Scenario:** An upstream "router" agent classifies a user request and returns a list of candidate systems with confidence scores. Dispatch to each system above a threshold.

**Steps:**

1. **Router agent** emits JSON via **JSON Schema** (Method 1). Schema:
   ```json
   {
     "name": "routing_decision",
     "schema": {
       "type": "object",
       "properties": {
         "systems": {
           "type": "array",
           "items": {
             "type": "object",
             "properties": {
               "system_name": {"type": "string"},
               "confidence": {"type": "number"},
               "rationale": {"type": "string"}
             },
             "required": ["system_name", "confidence", "rationale"],
             "additionalProperties": false
           }
         },
         "user_intent": {"type": "string"}
       },
       "required": ["systems", "user_intent"],
       "additionalProperties": false
     },
     "strict": true
   }
   ```
2. Save router output to `Local.RouterResult`.
3. Optional sanity message: `{Text(CountRows(Local.RouterResult.systems))} candidates returned`.
4. **For each** node over `Local.RouterResult.systems`.
5. Inside loop, **If/else** with condition `ThisRecord.confidence > 0.8`.
6. Inside true branch, **Invoke Agent** for the relevant system. Pass `ThisRecord.system_name`, `ThisRecord.rationale`, and `Local.RouterResult.user_intent` as inputs.
7. End step returns a wrapped object — never a bare array.

This sidesteps Parse Value entirely, sidesteps the array-stripping bug, and gives you typed Power Fx access throughout.

---

## References

- **Foundry Workflow docs:** https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/workflow
- **Bug discussion (Foundry):** https://github.com/orgs/microsoft-foundry/discussions/218
- **Bug issue (Agent Framework):** https://github.com/microsoft/agent-framework/issues/4195
- **Power Fx working with JSON:** https://learn.microsoft.com/en-us/power-platform/power-fx/working-with-json
- **Power Fx untyped object:** https://learn.microsoft.com/en-us/power-platform/power-fx/untyped-object
- **Copilot Studio variables (foundation Foundry is built on):** https://learn.microsoft.com/en-us/microsoft-copilot-studio/authoring-variables
- **Copilot Studio Parse Value examples:** https://learn.microsoft.com/en-us/microsoft-copilot-studio/guidance/adaptive-cards-display-data-from-arrays
- **Power Fx formula reference (Copilot Studio):** https://learn.microsoft.com/en-us/power-platform/power-fx/formula-reference-copilot-studio

---

## Quick TL;DR for the Next LLM

1. Foundry workflows = Copilot Studio + Power Fx. Variables use `System.` or `Local.` prefixes. Arrays = "Tables", Objects = "Records".
2. **Always wrap top-level outputs in an object — never return a bare array.**
3. **Prefer JSON Schema output on the agent node** over Parse Value. It parses automatically and avoids the open array-stripping bug.
4. If you must use Parse Value with arrays, edit the YAML directly using `kind: Table` for arrays inside `kind: Record`.
5. `ParseJSON()` and `JSON()` are NOT supported in workflows. Don't try them.
6. Create arrays with `Table({...}, {...})`. Append with `Collect()`. Access with `First()`, `Index()`, `Filter()`, `LookUp()`, `ForAll()`.
7. Inside a For Each node, the iterator is `ThisRecord`.
8. Bare scalar arrays like `[1,2,3]` become `[{Value:1},{Value:2},{Value:3}]` — reference as `.Value`.
9. Save explicitly. Use YAML view for debugging type mismatches.
