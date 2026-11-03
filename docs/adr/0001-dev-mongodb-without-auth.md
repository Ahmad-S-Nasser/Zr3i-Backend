# 0001 Development MongoDB runs without auth

Date: 2026-11-03 Â· Status: accepted for development only

## Context
The plan (W1-2) and CLAUDE.md rule 5 call for a single-node replica set with auth, so the application role can be limited to find and insert on append-only collections. The developer's local MongoDB is already installed without auth and without a replica set.

## Decision
- Local development may run without auth. Connection strings come from user-secrets or environment variables, never from the repo.
- Append-only is enforced in code from W1-2: the append-only helper exposes only insert and find, and an architecture test fails the build if anything else references the Mongo driver.
- Transactions need a replica set. Enable `--replSet rs0` locally before the first task that needs a transaction.
- Database-level enforcement (auth plus an insert-and-find role) is required in CI integration tests, staging and production. No deployment may run without it.

## Consequences
Local runs cannot prove the database-level role. Gate 1 must be checked once against an auth-enabled instance.

## Update 2026-11-03
The owner decided local MongoDB stays without auth until they ask for it. Do not add auth or role setup unprompted.

