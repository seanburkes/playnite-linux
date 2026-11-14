# Gate evidence

One file per gate: `P<phase>-<name>.md`. A gate without an artifact is not a gate.

Template:

```markdown
# Gate P<phase> — <name>

- Date:
- Owner:
- Reviewer(s):
- Commands run:
- Result: pass | fail | pass-with-exceptions

## Evidence
- <artifact path> — <what it proves>
- <CI run URL/ID> — <job>

## Counts
- Tests: <n> (baseline <n>)
- Assertions: <n> (baseline <n>)
- Skips: <n> (must be 0)
- Ported parity rows: <n> / <total in scope>

## Exceptions
| id | reason | owner | expiry |
| --- | --- | --- | --- |

## Decision
- [ ] Proceed to Phase <n>
- [ ] Hold: <reason>
```
