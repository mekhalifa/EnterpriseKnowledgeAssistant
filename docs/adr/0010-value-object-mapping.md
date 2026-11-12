# ADR-0010: Converters for single-value objects, owned types for composites

- **Status:** Accepted
- **Date:** 2026-11-12
- **Deciders:** project owner

## Context

Step 2 produced nine value objects. Step 3 had to store them without putting EF attributes
on `EKA.Domain`, which the architecture tests forbid.

EF Core offers three mechanisms, and they are not interchangeable:

| Mechanism | Shape in the database |
|---|---|
| `ValueConverter` | One column, holding a primitive |
| Owned type (`OwnsOne` / `OwnsMany`) | Several columns on the owner's table, or a separate table |
| Complex type (`ComplexProperty`) | Several columns, no identity |

## Decision

Split by the value object's **arity**:

**Single-value objects become converters, registered once by CLR type** in
`EkaDbContext.ConfigureConventions`: `TenantId`, `DocumentTitle`, `ChunkIndex`,
`TokenCount`, `AuthorityScore`, `SimilarityScore`.

Registering by type rather than per property means a new entity using `TenantId` is mapped
correctly without anyone remembering. Per-property registration is how one property
somewhere ends up unconverted, surfacing months later as a confusing "cannot map type"
error. Lengths are declared alongside, because a converted string without `HaveMaxLength`
silently becomes `nvarchar(max)` and cannot be indexed.

**Composite value objects become owned types**: `FileMetadata` and `PageRange` as
`OwnsOne` (columns on the owner's row), `Citation` as `OwnsMany` to a `MessageCitations`
table.

`Citation` is a collection and goes to a table rather than a JSON column because Step 11
must query citations to measure citation correctness — "which documents does the assistant
actually cite?" — and a relational table answers that with a join instead of `OPENJSON`.

**Encapsulated collections use `PropertyAccessMode.Field`.** `Document.Chunks` returns
`_chunks.AsReadOnly()`, which EF cannot add to. Telling EF to use the backing field means
it writes `_chunks` directly and never calls the getter, so the domain keeps its
encapsulation with no persistence-shaped compromise.

**`User.Roles` is a primitive collection** stored as a JSON array in one column. The Step 9
ACL filter runs against Qdrant, not SQL — the flow is "load the user once, pass their role
list into the vector-store filter" — so roles never need joining or indexing relationally.

## Consequences

### Positive

- `WHERE TenantId = @p0` is an index seek on an ordinary `uniqueidentifier` column. Type
  safety in C#, no cost in SQL.
- One registration point; adding an entity cannot forget a conversion.
- `ContentHash` is `char(64)` rather than `nvarchar(max)` — verified by a test — making the
  idempotency index possible at all.
- The domain has zero EF attributes, enforced by the architecture tests.

### Negative / costs we accept

- **Converted properties cost queryability in one specific way**: EF cannot translate
  operations on the *inside* of the value object. `WHERE Title.Value LIKE '%policy%'`
  requires the converter's column, not the wrapper's semantics.
- **Owned types cannot be bound to constructor parameters.** This bit immediately: `Citation`
  takes a `PageRange`, and EF failed model building with "no suitable constructor was
  found" until a private parameterless constructor was added for materialization.
- Materialization runs each value object's constructor, so invalid database rows throw at
  read time. Deliberate — loud beats silent — but it means a bad row fails a query rather
  than being filtered out.
- Owned-type columns are configured by string column names, which refactoring tools do not
  rename.

## Alternatives considered

**Everything as owned types.** Uniform, and keeps multi-property options open. Rejected: a
one-property owned type produces either an awkward nested column name or a pointless extra
table, and loses clean index support for the single most-filtered column in the system.

**Everything as converters, encoding composites into one string** (`PageRange` as
`"12-14"`, `FileMetadata` as JSON). Briefly considered when the `Citation` constructor-
binding error appeared, because it would have avoided the issue entirely. Rejected: it
trades a queryable, typed schema for opaque blobs, and the real fix — a parameterless
constructor — is the same pattern every entity already uses.

**`ComplexProperty` for composites.** The newer, arguably more correct mechanism for value
objects, since complex types have no identity and owned types technically do. Rejected for
now because owned types are long-established with predictable behaviour for optional
dependents and owned collections, and this step already had enough novelty. Worth revisiting
once the model is stable.

**Citations as a JSON column.** Simpler and adequate today. Rejected because Step 11's
evaluation needs to query them; changing storage shape later would mean a data migration.

## Revisit when

- Step 11 defines its evaluation queries — confirm `MessageCitations` supports them.
- `ComplexProperty` becomes the clearly better mechanism for `FileMetadata` and `PageRange`.
- Role-based filtering needs to happen in SQL, which would turn `User.Roles` into a table.
