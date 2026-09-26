# Long-Term Seeding Content Directory Status — 2026-09-26

This file records the Server-side implementation merged by the long-term seeding prototype.

## Feature branch

- `feat/long-term-seeding-directory`
- based directly on `master`
- CI: Restore / Build / Test green before merge

## Implemented control plane

The Content Directory provides:

- logical Content objects with multiple cryptographic identities;
- node inventory registration with generation conflict protection and registration idempotency;
- node lease / heartbeat and expired-node cleanup;
- observed-source-address endpoint policy;
- bounded content lookup;
- wakeup queue, polling, completion, retry backoff and seed-ready TTL;
- canonical BEP 52 metadata cache and binary manifest retrieval;
- SQLite / MySQL provider wiring;
- privacy-preserving ResourceKey observations and bounded ResourceKey lookup.

## ResourceKey model

ResourceKey is discovery metadata, not content identity.

The Server accepts a ResourceKey observation only from a currently leased node that owns the referenced content in its registered inventory. The observation maps the hashed resource descriptor to an existing Content object.

Implemented behavior includes:

- node resource announcement;
- ResourceKey normalization / validation;
- ResourceKey -> candidate Content lookup;
- multiple candidates where observations disagree;
- observation counts and last-observed timestamps;
- same-node remapping when a resource changes;
- invalidation when the node removes the content from its inventory;
- tests covering ownership, remapping and lookup semantics.

Raw HTTP URLs are not required by this API. Secret-bearing URLs therefore do not need to be persisted by the Directory.

## Canonical publication validation

Canonical torrent publication requires an authoritative registered BEP 52 file-root identity.

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

A full inventory refresh is differential by ContentId.

For unchanged content the Server preserves pending wakeup state, retry state and seed-ready TTL. Content removed from the refreshed inventory loses node presence and its node-scoped ResourceKey observations.

## Current API surface

Base route:

```text
/api/v1/content
```

The current controller includes:

- node registration;
- heartbeat;
- content lookup with optional prepare / wakeup;
- wakeup polling and completion;
- canonical manifest retrieval;
- node ResourceKey announcement;
- ResourceKey lookup.

The controller/contracts remain the wire-schema authority.

## Remaining production security work

This is still an alpha control plane. Production hardening remains outside this prototype:

- node public-key identity;
- signed / authenticated control requests;
- peer tickets;
- verified endpoint reachability;
- NAT traversal integration;
- rate limiting / abuse quotas;
- production EF migrations / retention policy.

The current endpoint proof is the observed control-connection IP plus a client-declared listening port; it is not a reachability proof.
