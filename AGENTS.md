# AGENTS.md

## Comparison rules

These apply to every check that compares EF Core's configuration with the database.

### 1. Set on both sides or neither

- A setting EF Core doesn't set must not be set in the database, and one EF Core sets must be set in the database.
- This holds even for default values. `fillfactor=100` in the database doesn't match EF Core not setting `fillfactor`, and `HasStorageParameter("fillfactor", 100)` doesn't match a table without it.
- Exception: if the database stores a value whether or not it was set, so it can't tell the two apart, compare effective values and treat a setting EF Core doesn't set as its default. Identity sequence options are like this.

### 2. Values must match exactly

- Compare values as exact text, as the database stores them. Values that mean the same still differ: `on` doesn't match `true`, and `0.050` doesn't match `0.05`.
- Exception: allow only changes the database itself makes when it stores a value, e.g. lower-casing an unquoted name, or rewriting a check constraint's expression.
