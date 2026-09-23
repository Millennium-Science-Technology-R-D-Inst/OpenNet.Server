# Long-Term Seeding Content Directory Status — 2026-09-24

This file is the Server-side handoff note for OpenNet long-term seeding.

## Branch / commit

Feature branch:

- `feat/long-term-seeding-directory`

Implementation checkpoint:

- `6b344c710092b1c7e41cb9f698abfb02f8d30a48`
- `feat: implement long-term seeding content directory and wakeups`

Based directly on:

- `master` at `c235cc09008c660cf5a7a0bfecdc6d81bbbf6b68`

The CI run for `6b344c710...` passed Restore, Build, and Test.

## Matching client branch

Client repository:

- https://github.com/hoshiizumiya/OpenNet

Client feature branch:

- `feat/long-term-seeding-content-catalog`

Client implementation checkpoint:

- `6eecbc78a89b9c05af7f616f5eeb814e12ebf45f`

The client branch also contains a later documentation handoff commit. Always read the latest feature-branch HEAD before continuing development.

## Implemented control-plane behavior

The Content Directory currently provides:

- logical Content objects;
- multiple cryptographic identities per Content;
- node inventory registration;
- generation conflict protection;
- registration idempotency;
- node lease and heartbeat;
- observed-source-address endpoint policy;
- bounded content lookup;
- node/content presence records;
- wakeup queue;
- wakeup polling;
- wakeup success/failure completion;
- retry backoff;
- seed-ready TTL;
- canonical v2 metadata cache;
- binary canonical manifest endpoint;
- expired-node cleanup;
- SQLite/MySQL provider wiring.

## Canonical publication validation

The Server does not trust a seeder-supplied canonical torrent merely because the node knows the content.

Publication requires an authoritative registered BEP 52 file-root identity.

The Server validates:

- canonical protocol version;
- canonical bencode structure;
- fixed `OpenNet.Content.v1` name/path;
- fixed 1 MiB piece length;
- exact content size;
- raw SHA-256 of the bencoded `info` dictionary;
- claimed v2 info-hash;
- BEP 52 pieces root;
- piece-layer length;
- piece-layer Merkle reduction to the registered file root.

Conflicting canonical metadata for a logical Content is rejected.

## Inventory refresh semantics

A full inventory refresh is differential with respect to `ContentId`.

For unchanged content, the Server preserves:

- pending wakeup state;
- retry state;
- current seed-ready TTL.

Only content that actually disappeared from the refreshed node inventory loses its presence.

This prevents a normal catalog revision from tearing down the logical readiness state of a just-woken seed.

## SQLite DateTimeOffset fix

EF Core SQLite cannot directly translate all relational comparisons on `DateTimeOffset`.

The final implementation configures UTC Unix-millisecond value converters for Content Directory time fields so queries such as:

- lease expiration;
- wake request lifetime;
- retry eligibility;
- seed-ready expiration

remain database-side and work on SQLite.

This issue was caught by the first CI test run; the final checkpoint is green.

## Current API surface

Base route:

```text
/api/v1/content
```

Current operations include:

- node registration;
- heartbeat;
- content lookup with optional prepare/wakeup;
- node wakeup polling;
- wakeup completion;
- canonical manifest retrieval.

Read the current controller/contracts instead of treating this summary as a wire-schema substitute.

## Security boundaries not yet solved

The current directory is still an alpha control plane.

Not yet implemented:

- node public-key identity;
- signed/authenticated control requests;
- peer tickets;
- verified endpoint reachability;
- NAT traversal integration;
- rate limiting / abuse quotas;
- production EF migrations / retention policy.

The current endpoint proof is only the observed control-connection IP combined with a client-declared listening port. It is not a reachability proof.

## Next Server milestone

The next Server-side feature should support privacy-preserving HTTP resource hints:

```text
ResourceKey -> ContentId
```

This mapping must stay separate from cryptographic ContentIdentity.

Requirements:

- do not persist raw secret-bearing signed URLs;
- model a hashed/canonicalized resource descriptor;
- support validators such as strong ETag and Content-Length as hints;
- keep observation/expiry metadata;
- define change/conflict semantics;
- keep lookup bounded;
- reuse existing ContentObject identities;
- add tests for changed-resource and privacy cases.

The mapping is discovery metadata only. It must never replace BEP 52/content-hash verification.

## Development policy

Do not block feature development waiting for OpenNet client's long Windows Canary workflow.

Batch coherent changes, review them, commit them, then continue. Treat CI as asynchronous feedback and fix concrete failures in batches after logs are available.
