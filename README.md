# EfCore.SchemaCompare

If you are changing the schema of your database's schema outside of EF Core' migrations, say by using SQL change scripts, then this library can quickly tell you if the a specific database schema and EF Core's `Model` of the database are in step. Versions below 8  this library 

The first number in the version number of this library defines what version of NET framework it works for. e.g. 

- EfCore.SchemaCompare version 10.?.? works with to NET 10. 
- EfCore.SchemaCompare version  9.?.? works with to NET 9. 
- EfCore.SchemaCompare version  8.?.? works with to NET 8. 
- EfCore.SchemaCompare version  7.?.? works with to NET 7 ... an so 

**WARNING**: Version 8 any above contains a **BREAKING CHANGE**. You need to add the Microsoft.EntityFrameworkCore.Design NuGet to your application when using this library. This is needed as the EfCore.SchemaCompare library now supports multiple database providers does - see [What database providers does it support](https://github.com/JonPSmith/EfCore.SchemaCompare/blob/master/README.md#what-database-providers-does-it-support).

The EfCore.SchemaCompare library (shortened to EfSchemaCompare in the documentations) is available on [NuGet as EfCore.SchemaCompare](https://www.nuget.org/packages/EfCore.SchemaCompare/) and is an open-source library under the MIT licence. See [ReleaseNotes](https://github.com/JonPSmith/EfCore.SchemaCompare/blob/master/ReleaseNotes.md) for details of changes and information on versions from EF Core 5 onwards.

**TABLE OF CONTENT**

1. [What database providers does it support](#what-database-providers-does-it-support)
2. [What does EfSchemaCompare check?](#what-does-EfSchemaCompare-check)
3. [List of limitations](#List-of-limitations)
4. [Introduction to how EfSchemaCompare works](#Introduction-to-how-EfSchemaCompare-works)
5. [How to use EfSchemaCompare](#How-to-use-EfSchemaCompare)
5. [Different parameters to the `CompareEfWithDb` method](#different-parameters-to-the-compareefwithdb-method)
6. [Understanding the error messages](#Understanding-the-error-messages)
7. [How to suppress certain error messages](#How-to-suppress-certain-error-messages)
8. [Other configuration options](#Other-configuration-options)

**NOTE:** I use the term *entity class* for classes mapped to the database by EF Core.

## What database providers does it support

- Version 8 now compares all EF Core database providers that can be [be scaffolded](https://learn.microsoft.com/en-us/ef/core/managing-schemas/scaffolding/). However some database providers may show incorrect match errors, because some database providers don't follow the SqlServer style, e.g. CosmosDB 

_Thanks to GitHub @bgrauer-atacom and @lweberprb for suggesting that this library could support extra database providers. See the [issue #26](https://github.com/JonPSmith/EfCore.SchemaCompare/pull/26) to see the code that these two people provided to add this feature._

- Versions below 8 support:
  - SqlServer
  - Sqlite
  - Npgsql.EntityFrameworkCore.PostgreSQL

## What does EfSchemaCompare check?

Names and values must be the same text, character for character. For example, a comment `Total price` doesn't match `Totalprice`, and `café` written with the single letter `é` doesn't match `café` written as an `e` followed by a combining accent. The only exceptions are changes the database itself makes:

- Outer brackets around a default's or computed column's SQL are left out, as databases add them when they store it.
- On PostgreSQL, a default's, a computed column's and a check constraint's SQL are compared as expressions, as PostgreSQL adds brackets, spaces and casts when it stores them. For example, `price * quantity` matches `(price * (quantity)::numeric)`. SQL that can't be parsed is compared as exact text.
- Spaces between a type's size, precision and scale don't matter, as the database stores these as numbers, e.g. `decimal(9,2)` matches `decimal(9, 2)`.

### Stage 1 - checks on EF Core side

- **Database:** collation (`modelBuilder.UseCollation`). The scaffolder only provides the database's collation if it isn't the server's default, so a model without one matches a database using the default.
- **Table/View exists**: That a table or view that an entity class is mapped exists. This checks both table/view name and schema name. A table's comment (`ToTable(t => t.HasComment(...))`) is compared too.
- **Property/Column:**  exists, database type (including size and precision), nullability, computed column (including persistence), column default value, when updated (e.g. column is updated `OnAdd` for a int primary key which is provided by the database). For table columns: collation (`UseCollation`) and comment (`HasComment`), where a collation or comment that EF Core doesn't set must not be set in the database, even to the database's default collation.
- **Column compression and identity (PostgreSQL only):**
  - The compression method (`UseCompressionMethod`) must match. One that EF Core doesn't set must not be set in the database, even to the default `pglz`.
  - How the database generates the column's value must match: `IdentityAlwaysColumn` (`UseIdentityAlwaysColumn`), `IdentityByDefaultColumn` (`UseIdentityByDefaultColumn`, EF Core's default for an integer key), `SerialColumn` (`UseSerialColumn`) or `None`. Strategies that don't change the column, e.g. `UseHiLo`, count as `None`.
  - An identity column's sequence options (`HasIdentityOptions`) must match: start, increment, min, max, cycle and cache. PostgreSQL stores every option's value, so it can't tell an option set to its default from one that wasn't set. An option EF Core doesn't set is therefore compared as its default value.
  - Typical errors: `DIFFERENT: Widget->Property 'Id', value generation strategy. Expected = IdentityAlwaysColumn, found = IdentityByDefaultColumn` and `DIFFERENT: Widget->Property 'Id', identity options. Expected = increment=5, found = increment=10`
- **Primary key:** SQL constraint name, columns in order (a different order or an extra column in the database is reported, e.g. `DIFFERENT: Pair->PrimaryKey 'pk_pair', columns. Expected = second,first, found = first,second`)
- **Foreign keys:** SQL constraint name, Delete behavior, columns in order, and the columns it references in order (e.g. `DIFFERENT: Child->ForeignKey 'fk_child_parent', columns. Expected = c1,c2, found = c2,c1` and `... principal columns. Expected = p1,p2, found = p2,p1`)
- **Indexes:** SQL constraint name, Unique/not unique, columns in order (a different order or an extra column in the database is reported, e.g. `DIFFERENT: Pair->Index 'first', columns. Expected = first, found = first,second`). Included columns, e.g. `IncludeProperties`, aren't part of an index's columns. An index's collations (`UseCollation` on an index) are compared too. A unique index (`HasIndex(...).IsUnique()`) only matches a unique index in the database, not a `UNIQUE` constraint.
- **Index INCLUDE columns and NULLS NOT DISTINCT (PostgreSQL only):**
  - An index's `INCLUDE` columns (`IncludeProperties`) must match, in order. An index EF Core doesn't give any must not have any in the database, e.g. `DIFFERENT: Account->Index 'name', include columns. Expected = <null>, found = email`.
  - A unique index's `NULLS NOT DISTINCT` (`AreNullsDistinct(false)`) must match. PostgreSQL stores whether nulls are distinct whether or not it was set, so EF Core not setting it is compared as the default, `NULLS DISTINCT`, e.g. `DIFFERENT: Account->Index 'code', nulls distinct. Expected = NULLS DISTINCT, found = NULLS NOT DISTINCT`.
- **Settings EF Core can't set (PostgreSQL only):** EF Core never creates these, so each one on a table the DbContext maps is reported as `EXTRA IN DATABASE`. They are found in stage 1, so they are reported even if there are other errors:
  - An index on an expression, e.g. `CREATE INDEX ... (lower(email))`. The scaffolder leaves these out, so they aren't found with the other unused indexes in stage 2, e.g. `EXTRA IN DATABASE: Index 'account', index constraint name. Found = ix_lower_email`.
  - Row-level security being enabled or forced, and its policies, e.g. `EXTRA IN DATABASE: Table 'account', row level security. Found = ENABLE ROW LEVEL SECURITY` and `EXTRA IN DATABASE: Table 'account', policy. Found = p_all`.
  - Rules, e.g. `EXTRA IN DATABASE: Table 'account', rule. Found = r_no_delete`.
  - A primary key, `UNIQUE` constraint or foreign key that is `DEFERRABLE`, and a foreign key that is `NOT VALID`, e.g. `EXTRA IN DATABASE: Table 'account', deferrable. Found = fk_account_owner DEFERRABLE INITIALLY DEFERRED`.
  - `INCLUDE` columns on a primary key or `UNIQUE` constraint, and `NULLS NOT DISTINCT` on a `UNIQUE` constraint, as EF Core can only set these on an index, e.g. `EXTRA IN DATABASE: Table 'account', include columns. Found = pk_account INCLUDE (email)`.
- **Alternate keys:** EF Core creates a `UNIQUE` constraint for an alternate key (`HasAlternateKey`, or a key a foreign key references with `HasPrincipalKey`). This checks its SQL constraint name and columns in order, e.g. `DIFFERENT: Widget->AlternateKey 'ak_widget_code_name', columns. Expected = code,name, found = name,code`. Sqlite's scaffolder doesn't read a unique constraint's name, so on Sqlite an alternate key is reported as `NOT CHECKED`.
- **Functions and triggers (PostgreSQL only):** EF Core can only declare these (`HasDbFunction`, `ToTable(tb => tb.HasTrigger(...))`), not hold their SQL, so this checks by identity, not body:
  - Every declared function/trigger must exist. Functions are matched on schema, name *and* argument types, e.g. `public.slugify(text)`, so each overload must be declared. Triggers are matched on schema, table and name.
  - A declared function that is built into PostgreSQL or comes from an extension, e.g. `HasDbFunction(...).HasName("jsonb_extract_path_text")`, only needs its name to exist, because PostgreSQL resolves calls to these via variadic arguments, implicit casts, and casts written as calls such as `timestamptz(text)`. A function you create yourself with the same name is still compared on its full signature.
  - Every function, procedure, aggregate and trigger in the database that isn't declared by one of the DbContexts is reported as `EXTRA IN DATABASE`, on all tables, not just mapped ones. Only objects built into PostgreSQL and objects owned by an extension are skipped. A trigger's function (`RETURNS trigger`) can't be declared via `HasDbFunction`, so it is always reported - suppress it with `IgnoreTheseErrors` if you want to keep it.
  - Typical errors: `EXTRA IN DATABASE: Function 'public.touch_widget()'. Found = function returning trigger` and `EXTRA IN DATABASE: Trigger 'public.widget.trg_widget_touch'. Found = CREATE TRIGGER ...`
  - Requires PostgreSQL 13 or later.
- **Storage parameters (PostgreSQL only):** the storage parameters set via `HasStorageParameter` on an entity or an index, e.g. `fillfactor` or `autovacuum_vacuum_scale_factor`, must match the ones on the table or index in the database. A parameter that EF Core doesn't set must not be set in the database:
  - A parameter must be set on both sides or neither, even when one side sets it to PostgreSQL's default. For example, a table with `fillfactor=100` in the database is reported as `EXTRA IN DATABASE` if EF Core doesn't set `fillfactor`, and `HasStorageParameter("fillfactor", 100)` is reported as `NOT IN DATABASE` if the table doesn't set it.
  - Values must be the same text. PostgreSQL holds a value as the text it was set with, so `on` doesn't match `true`, and `0.050` doesn't match `0.05`, even though PostgreSQL reads them the same.
  - A table's TOAST parameters are compared too, under the names used to set them, e.g. `toast.autovacuum_enabled`.
  - Only tables mapped by the DbContext and indexes declared in EF Core are checked, e.g. not a primary key's index, as EF Core can't set storage parameters on it.
  - Typical errors: `DIFFERENT: Table 'widget', storage parameter. Expected = fillfactor=70, found = fillfactor=50`, `EXTRA IN DATABASE: Table 'widget', storage parameter. Found = toast.autovacuum_enabled=false` and `NOT IN DATABASE: Widget->Index 'name', storage parameter. Expected = fillfactor=80`

### Stage 2 - checks on database

- Unused tables or views, which are reported as warnings, not errors - see [Warnings](#warnings)
- Unused columns
- Unused Indexes
- Unused unique constraints, i.e. ones no alternate key creates, e.g. `EXTRA IN DATABASE: AlternateKey 'widget', constraint name. Found = uq_widget_name`

### It check the following EF Core features

- Normal classes/properties
- Keyless classes
- Backing fields
- Shadow properties
- Value Converters
- Owned Types
- Table-per-Hierarchy
- Table splitting
- Concurrency tokens
- Json Mapping (`ToJson`): the column holding the JSON is compared like any other column (exists, database type and nullability), using the column name `ToJson` gives it. The JSON's contents aren't checked.

## List of limitations

- The EF Core's scaffolder doesn't read in any index on the foreign key (the scaffolder assumes EF Core will do that by default). That means I can't check that there is an index on a foreign key.
- Cannot correctly check Table-per-Type or Table-per-Class classes because EF Core doesn't currently hold that data. This is tracked by [Ef Core #19811](https://github.com/dotnet/efcore/issues/19811).
- Cannot compare database tables/columns using InvariantCultureIgnoreCase. That is a EF Core 5+ limitation.
- EfCore.SchemaCompare versions below 8 only support SQL Server, Sqlite and PostgresSql, but version 8 supports all EF Core database providers that can be be scaffolded. However some database providers may show incorrect match errors, because some database providers don't follow the SqlServer style, e.g. CosmosDB
- The EF Core 8 [new features](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-8.0/whatsnew) aren't compared in this release. I may add some later. 

The following are things I haven't bothered to check.

- Checking of sequences

## Introduction to how EfSchemaCompare works

EfSchemaCompare uses two EF Core features to get EF Core's internal schema and the database's schema. They are

1. EF Core's `Model` property in your application's DbContext. This provides the internal version of the database schema that EF Core builds from looking at the entity classes and any EF Core configuration attributes/methods.
2. EF Core's [Reverse Engineering](https://docs.microsoft.com/en-us/ef/core/managing-schemas/scaffolding) service, which allows me to access an actual database schema.

The fun part is comparing these two sources, especially with all the different types of configurations that EF Core can handle. The diagram shown below shows using EfSchemaCompare to check a test database that you updated with some SQL migration scripts against the current EF Core's `Model`.

![EfSchemaCompare diagram](https://github.com/JonPSmith/EfCore.SchemaCompare/blob/master/EfSchemaCompare.png)

The EfSchemaCompare uses two stages: Stage 1 checks your EF Core DbContext matches your database. Stage 2 checks your database for extra tables, columns, etc. that your EF Core DbContext doesn't use.

## How to use EfSchemaCompare

I usually run the EfSchemaCompare code in my unit tests, but that is up to you.

Here is a example of using the EfSchemaCompare feature

```c#
[Fact]
public void CompareViaContext()
{
    //SETUP
    var options = //... with connection to database to check
    using (var context = new BookContext(options))
    {
        var comparer = new CompareEfSql();

        //ATTEMPT
        //This will compare EF Core model of the database with the database that the context's connection points to
        var hasErrors = comparer.CompareEfWithDb(context); 

        //VERIFY
        //The CompareEfWithDb method returns true if there were errors, and false only if nothing differs.
        //The comparer.GetAllErrors property returns a string, with each error on a separate line
        hasErrors.ShouldBeFalse(comparer.GetAllErrors);
    }
}
```

### Different parameters to the `CompareEfWithDb` method

1. The `CompareEfWithDb` method can take multiple DbContexts, known as *bounded contexts* (see chapter 13, section 13.4.8 in my book [Entity Framework Core in Action, second edition](https://bit.ly/EfCoreBookEd2)). You can add as many contexts and they are compared to one database.
2. You can also provide a string that points to the database as the first parameter. It can have two forms:
   - It will use the string as a connection string name in the test's `appsetting.json` file.
   - If no connection string is found in the `appsetting.json` file, or there is no `appsetting.json`, then it assumes the string is a connection string.

See below for an example of both of of these options:

```c#
[Fact]
public void CompareBookThenOrderAgainstBookOrderDatabaseViaAppSettings()
{
    //SETUP
    //... I have left out how the options are created
    //This is the name of a connection string in the appsetting.json file in your test project
    const string connectionStringName = "BookOrderConnection";
    using (var context1 = new BookContext(options1))
    using (var context2 = new OrderContext(options2))
    {
        var comparer = new CompareEfSql();

        //ATTEMPT
        //Its starts with the connection string/name  and then you can have as many contexts as you like
        var hasErrors = comparer.CompareEfWithDb(connectionStringName, context1, context2);

        //VERIFY
        hasErrors.ShouldBeFalse(comparer.GetAllErrors);
    }
}
```

## Understanding the error messages

The `comparer.GetAllErrors` property will return a string with each error separated by the `Environment.NewLine` string. Below is an example of an error

```text
"DIFFERENT: MyEntity->Property 'MyString'. Expected = varchar(max), found = nvarchar(max)"
```

The error above says

- `DIFFERENT:` There is a difference between EF Core and the database  (other settings are `NotInDatabase`, `ExtraInDatabase`)
- `MyEntity->Property 'MyString', column type` gives a description of what was checked
- `Expected = varchar(max)` says what EF Core thought it should be
- `found = nvarchar(max)` says what the database setting was

Here is another error coming from stage 2 where it checks the database side, i.e., Unused Tables, Columns and Indexes

```text
EXTRA IN DATABASE: Table 'HeadEntries', column name. Found = DifferentColumnName
```

This says that there is a column called `MyEntityId` in the table `MyEntites` that hasn't got a property in the entity class mapped to the `MyEntites` table.

*NOTE: When errors contain the word `Table` it can be a SQL Table or View.*

### Warnings

A table or view in the database that no DbContext maps to is reported as a warning, not an error, as a database often has tables EF Core doesn't use. Warnings don't make `CompareEfWithDb` return true. The `comparer.GetAllWarnings` property returns them in the same format as errors, e.g.

```text
EXTRA IN DATABASE: Table 'audit_log'
```

Warnings come from stage 2, so they are only looked for if stage 1 found no errors, or `AlwaysRunStage2` is true.

## How to suppress certain error messages

In a few cases you will get errors that aren't correct (see limitations) or not relevant. In these cases you might want to suppress those errors. There are two way to do this, with the first being the easiest. Both use the `CompareEfSqlConfig` class.

### Suppress errors via `IgnoreTheseErrors`

In this approach you capture the error strings you want to ignore and return them as a string, with each error separated by the newline, `'\n'`, character. You feed the errors via the configuration's `IgnoreTheseErrors` method. See an example below

```c#
        public void CompareTptContextSuppressViaIgnoreTheseErrors()
        {
            //SETUP
            var options = this.CreateUniqueClassOptions<TptDbContext>();
            using var context = new TptDbContext(options);
            context.Database.EnsureClean();

            var config = new CompareEfSqlConfig();
            //This converts the error strings back CompareLog classes (see next example) which suppresses these errors 
            config.IgnoreTheseErrors(@"DIFFERENT: TptVer1->PrimaryKey 'PK_TptBases', constraint name. Expected = PK_TptBases, found = PK_TptVer1
DIFFERENT: TptVer1->Property 'Id', value generated. Expected = OnAdd, found = Never
DIFFERENT: TptVer1->Property 'MyVer1Int', nullability. Expected = NULL, found = NOT NULL
DIFFERENT: TptVer1->ForeignKey 'FK_TptVer1_TptBases_Id', delete behavior. Expected = ClientCascade, found = NoAction
DIFFERENT: Entity 'TptVer1', constraint name. Expected = PK_TptBases, found = PK_TptVer1
DIFFERENT: TptVer2->PrimaryKey 'PK_TptBases', constraint name. Expected = PK_TptBases, found = PK_TptVer2
DIFFERENT: TptVer2->Property 'Id', value generated. Expected = OnAdd, found = Never
DIFFERENT: TptVer2->Property 'MyVer2Int', nullability. Expected = NULL, found = NOT NULL
DIFFERENT: TptVer2->ForeignKey 'FK_TptVer2_TptBases_Id', delete behavior. Expected = ClientCascade, found = NoAction
DIFFERENT: Entity 'TptVer2', constraint name. Expected = PK_TptBases, found = PK_TptVer2");

            var comparer = new CompareEfSql(config);

            //ATTEMPT
            var hasErrors = comparer.CompareEfWithDb(context);

            //VERIFY
            hasErrors.ShouldBeFalse(comparer.GetAllErrors);
        }
    }
```

### Suppress errors via `AddIgnoreCompareLog`

The other approach is useful when you want to suppress a general set of errors, but it is a bit complicated. Here is an example where it suppresses any errors found on the default value set on a column.

```c#
[Fact]
public void CompareSuppressViaViaAddIgnoreCompareLog()
{
    //SETUP
    var options = this.CreateUniqueClassOptions<BookContext>();
    using var context = new BookContext(options);
    context.Database.EnsureClean();

    var config = new CompareEfSqlConfig
    config.AddIgnoreCompareLog(new CompareLog(CompareType.Property, CompareState.Different, null, CompareAttributes.DefaultValueSql));
    var comparer = new CompareEfSql(config);

    //ATTEMPT
    var hasErrors = comparer.CompareEfWithDb(context);

    //VERIFY
    hasErrors.ShouldBeFalse(comparer.GetAllErrors);
}
```

## Other configuration options

You have already seen the class called `CompareEfSqlConfig` for suppressing errors, but there are two other configrations. 

### `TablesToIgnoreCommaDelimited` string property

You have already seen the class called `CompareEfSqlConfig` for suppressing errors. There is one other configuration property called `TablesToIgnoreCommaDelimited`, which allows you to control what table/views in the database are considered. 

By default (i.e. when `TablesToIgnoreCommaDelimited` is null or "") `CompareEfSql` checks all the tables/views in the database. Ones that your EF Core entity classes aren't mapped to are reported as warnings (see [Warnings](#warnings)), which don't make `CompareEfWithDb` return true. The other option is:

- Set `TablesToIgnoreCommaDelimited` to a list of tables to leave out.  
If there are tables/views in your database that EF Core doesn't access, and you don't want a warning for them, you can tell `CompareEfSql`
to leave them out. You can also hide a warning with `IgnoreTheseErrors`, like an error.
You do this by providing a comma delimited list of table names, with an optional schema name if needed.
Here are two examples of a table name
  - `MyTable` - this has no schema, so the default schema of the database will be used
  - `dbo.MyTable` - this defines the schema to be `dbo`, - a full stop separates the schema name from the table name.

*NOTE: The comparison is case insensitive.*  

Here is an example of configuring the comparer to not look at the tables `Orders` and `LineItem`
```c#
var config = new CompareEfSqlConfig
{
    TablesToIgnoreCommaDelimited = "Orders,LineItem"
};
var comparer = new CompareEfSql(config);
```

### `AlwaysRunStage2` boolean property (v8.2.0 or later)

Getting all the errors in one go can be useful, for instance when you are creating a EF Core DbContext to match a given database. But by default, Stage 2 isn't run if Stage 1 found errors that haven't been register in `config.IgnoreTheseErrors(... your error strings ...)`. 

In version 8.2.0 a new boolean property called `AlwaysRunStage2` in `CompareEfSqlConfig` and if you you set this to `true` then Stage 2 will always run, even if there are non-ignored errors. See [issue #38](https://github.com/JonPSmith/EfCore.SchemaCompare/issues/38) which made me add this new feature.

Either way, `CompareEfWithDb` returns false only if nothing differs, i.e. `GetAllErrors` is empty. An error found by either stage makes it return true.
