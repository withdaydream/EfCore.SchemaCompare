# Release notes

## 10.3.0-daydream

- A declared function that is built into PostgreSQL or comes from an extension, e.g. `jsonb_extract_path_text`, is no longer reported as `NOT IN DATABASE`.
- New feature (PostgreSQL): compares the storage parameters set via `HasStorageParameter` on entities and indexes, e.g. `fillfactor`, with the database. A parameter EF Core doesn't set must not be set in the database, even to its default, and values must be the same text, e.g. `on` doesn't match `true` - see README.
- Indexes and primary keys are now compared on their columns in order. An index or key whose columns are in a different order, or that has extra columns in the database, was reported as OK, but is now reported as `DIFFERENT: ... columns. Expected = a,b, found = b,a`.
- NOTE: If your database has indexes or keys like that you will see new `DIFFERENT` errors. Fix the database or EF Core configuration, or add an ignore line for each one.
- New feature: compares collations (database, column and index) and comments (table and column). A collation or comment EF Core doesn't set must not be set in the database - see README.
- New feature (PostgreSQL): compares a column's compression method, how its value is generated (identity always, identity by default, serial or none), and an identity column's sequence options, e.g. its start value - see README.
- Changed (PostgreSQL): a column's compression method is no longer added to its database type. If you used `HasColumnType("text COMPRESSION lz4")` to make it match, use `UseCompressionMethod("lz4")` instead.
- NOTE: These checks may report differences that were hidden before, e.g. comments added by SQL scripts. Fix the database or EF Core configuration, or add an ignore line for each one.
- Foreign keys are now compared on their columns, and the columns they reference, in order. One whose columns are in a different order was reported as OK, but is now reported as `DIFFERENT: ... columns` or `DIFFERENT: ... principal columns`. Fix the database or EF Core configuration, or add an ignore line for each one.
- Fixed (PostgreSQL): a foreign key with more than one column made the comparison throw an exception, because the database's foreign keys were read as a row per pair of columns.
- New feature: compares alternate keys (`HasAlternateKey`) with the database's `UNIQUE` constraints, on their name and columns in order, and stage 2 reports `UNIQUE` constraints that no alternate key creates - see README.
- Changed: a unique index (`HasIndex(...).IsUnique()`) no longer matches a `UNIQUE` constraint with the same name, as EF Core creates a unique index for it, not a constraint.
- NOTE: If your database has `UNIQUE` constraints where EF Core expects unique indexes, or ones EF Core doesn't declare, you will see new errors. Fix the database or EF Core configuration, or add an ignore line for each one.
- Fixed: the column holding an entity mapped to JSON (`ToJson`) was marked OK without being checked. It is now compared on whether it exists, its type and its nullability, using the column name `ToJson` gives it, so one not named after its navigation is no longer reported as an extra column.
- Fixed (PostgreSQL): two tables with a check constraint of the same name made the comparison throw an exception, and a check constraint whose name ended in `not_null` was ignored, so it was reported as `NOT IN DATABASE`, or never reported if only the database had it. Only PostgreSQL's own NOT NULL constraints are left out now.

## 10.2.0-daydream

- New feature (PostgreSQL): compares functions and triggers declared via `HasDbFunction`/`HasTrigger` with the database, reporting any that are missing and any undeclared ones in the database - see README.
- Fixed: `IgnoreTheseErrors` dropped the `Found = ...` part of `EXTRA IN DATABASE` errors, so one ignore line matched every extra item of that kind with the same name, e.g. ignoring one extra column in a table ignored all extra columns in that table, and ignoring one extra check constraint ignored all of them. It now only ignores the exact item.
- NOTE: If you relied on that broad match you may see `EXTRA IN DATABASE` errors that were hidden before. Add an ignore line for each one, or use `AddIgnoreCompareLog` with `found` left as null to match them all.
- Fixed: an `Expected = ...` value containing ", ", e.g. a multi-column foreign key, was cut short, so its ignore line never matched.

## 10.0.0

- .NET 10 version

## 9.0.0

- .NET 9 version

## 8.2.0

- The ExtraInDatabase errors didn't correctly say what part of the database (e.g. "Table", "Column") was extra. This is fixed. 
- NOTE: If you have ignored some ExtraInDatabase errors you our old ignored ExtraInDatabase might need updating to the new (correct format) pattern.  
- New boolean 'AlwaysRunStage2' in the CompareEfSqlConfig. If set to 'true', then Stage 2 will always run, even if there are non-ignored errors. See issue #38, which made me add this new feature.


## 8.1.0

- New feature: Checks Json Mapping data, see README for more. Limitation that only works with the default column name
- Fixed issue #18. i.e "columns that are extra in db are not excluded" is fixed
- IEntityType is obsolete, moved to ITypeBase - see https://github.com/dotnet/efcore/issues/34594
- Updated vunables NuGets System.Text.Json and Microsoft.Extensions.Caching.Memory

## 8.0.4

- REALLY removed Microsoft.IdentityModel.Tokens NuGet as not correct (see issue #36)

## 8.0.3: ERROR - use 8.0.4

- Removed Microsoft.IdentityModel.Tokens NuGet as not correct (see issue #36)

## 8.0.2

- Removed vulnerable NuGets
- Removed EF Core database NuGets, e.g. Microsoft.EntityFrameworkCore.SqlServer, because its uses provided DbContext

## 8.0.1

- Fixed problems with TablesToIgnoreCommaDelimited with databases not supporting schema - see issue #30

## 8.0.0

- Supports .NET 8
- NEW FEATURE: Now compares all EF Core database providers, but some database providers may show incorrect match errors. Thanks to users GitHub bgrauer-atacom and @lweberprb for this feature.
- BREAKING CHANGE: You need to add the Microsoft.EntityFrameworkCore.Design NuGet to the application that uses this library 

## 8.0.0-rc2-0002

- Supports .NET 8-rc.2 - please try this version and report any problems
- NEW FEATURE: Now compares all EF Core database providers, but some database providers may show incorrect match errors. Thanks to users GitHub bgrauer-atacom and @lweberprb for this feature.
- BREAKING CHANGE: You need to add the Microsoft.EntityFrameworkCore.Design NuGet to your 
- Bug Fix: Fix to issue #21

## 7.0.0

- Updated to EF Core 7.0
 
## 6.0.2

- Fix to issue #21, but now catches "Not In Database" errors, as seen in issue #25

## 6.0.1

- Fix to issue #21

## 6.0.0

- Updated to EF Core 6.0 (thanks to Wade Baglin, GitHub @pleb)
- Added TemporalTable to compare (thanks to Wade Baglin, GitHub @pleb)

## 6.0.0-preview001

- Updated to .NET 6.0 preview RC.2 (thanks to Wade Baglin, GitHub @pleb)
- Added TemporalTable to compare (thanks to Wade Baglin, GitHub @pleb)

## 5.1.4

- Removed development dependency of package. It made it harder to EfCore.SchemaCompare in unit tests

## 5.1.3

- Make package a development dependency - see issue #4
- Updated the exception message about how to use another database type.

## 5.1.2

- Bug fix - looking for appsettings.json file throws an exception if it can't find the folder. See issue #3

## 5.1.1

- Bug fix - Table with incorrect name threw an exception. See issue #2

## 5.1.0

- Fixed bug in Owned Types with `IsRequired` where properties aren't forced to nullable

## 5.0.1

- Fixed bug in table checks

## 5.0.0

- Moved from EfCore.TestSupport to become a separate library 
- Big tidy up and improve this feature
- Improvement: Support for EF Core 5 database features
   - EF Core 5 - direct many-to-many
   - EF Core 5 - property bag
   - EF Core 5 - Same type mapped to different tables
   - EF Core 5 - persistent computed column
   - EF Core 5 - Entity to SQL query is marked as not checked
- New Feature: Checks Views
- Improvement: Uses IRelationalTypeMappingSource for constants - see EF Core #21731
- Bug fix: Nested Owned Type properties are tested as nullable
- Bug fix: Nested table splitting optional class properties as nullable
- Bug fix: TPH properties being nullable
- BREAKING CHANGE: Cannot compare tables/views using IgnoreCase

### Does not support

- Cannot detect Owned Type with Required option (i.e. not null)
- Cannot correctly check Table-per-Type classes
- You can no longer compare database entries using ignore case. (that is a EF Core 5 limitation)
- Checking of collations
- Checking of Alternative keys
- Checking of sequences


## Previous releases of EfSchemaCompare

This library started with EF Core 5. For EF Core below 5.0.0, for instance EF Core 3.x, of EF Core 2.1.x then use [EfCore.TestSupport](https://github.com/JonPSmith/EfCore.TestSupport)