# AGENTS.md

## Comparison rules

These apply to every check that compares EF Core's configuration with the database.

### 1. Set on both sides or neither

- A setting EF Core doesn't set must not be set in the database, and one EF Core sets must be set in the database.
- This holds even for default values. `fillfactor=100` in the database doesn't match EF Core not setting `fillfactor`, and `HasStorageParameter("fillfactor", 100)` doesn't match a table without it.
- This includes settings EF Core can't set at all, e.g. a row-level security policy. EF Core never creates one, so report it as extra in the database whenever the database has one.
- Exception: if the database stores a value whether or not it was set, so it can't tell the two apart, compare effective values and treat a setting EF Core doesn't set as its default. Identity sequence options, and an index's `NULLS [NOT] DISTINCT`, method, and each column's sort order, nulls order, operator class and collation are like this. An index column's collation defaults to its column's.

### 2. Values must match exactly

- Compare values as exact text, as the database stores them. Values that mean the same still differ: `on` doesn't match `true`, and `0.050` doesn't match `0.05`.
- Exception: allow only changes the database itself makes when it stores a value, e.g. lower-casing an unquoted name, or rewriting a check constraint's expression.

## Not yet supported

These aren't compared, or are compared wrongly:

- Table-per-type (TPT) inheritance: a derived table's primary key name, and whether its key's value is generated, are reported as different, even though EF Core created them correctly.
- Sequences (`HasSequence`).
- PostgreSQL enums (`HasPostgresEnum`) and extensions (`HasPostgresExtension`).
- Unlogged tables (`IsUnlogged`).
- A tsvector expression index (`IsTsVectorExpressionIndex`) is reported as not in the database, as the scaffolder leaves out indexes on expressions.
- Settings EF Core can't set other than the ones the README lists, e.g. comments on indexes and constraints, a column's statistics target or storage mode (`SET STATISTICS`, `SET STORAGE`), and types, e.g. domains, that no column uses.
- With `AlwaysRunStage2`, a column or index that differs is also reported as extra in the database, as stage 2 only counts the ones stage 1 marked OK as used.
