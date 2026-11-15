---
name: volo-abp
description: Develop and verify ABP Framework (Volo.Abp, abp.io) migration output and AbpToVolo target rules. Use for modularity, DDD layers, async repositories, Identity and TenantManagement integration, EF Core migrations, API compatibility, and runtime validation after migration from ASP.NET Boilerplate.
---

# Volo ABP target development

## Establish the target

1. Record the selected `Volo.Abp.*` version, target framework, EF provider, and Microsoft package versions. Check the selected release's package metadata and source signatures. Do not treat this repository's defaults as the latest supported release.
2. Read [the converter process](../../docs/development-process.fa.md) and [open decisions](../../docs/remaining-work-and-decisions.fa.md). Keep application-specific changes separate from general converter rules.
3. Preserve the selected compatibility contract: `ApiStyle=Boilerplate` or `Abp`, `IdType=Source` or `Guid`, migration mode, and host standardization. Do not switch these choices while fixing an unrelated compiler error.

## Apply target conventions deliberately

- Keep shared enums/constants/resources in Domain.Shared; DTOs and service interfaces in Application.Contracts; business rules in Domain; use cases in Application; persistence in EntityFrameworkCore; endpoints in HttpApi; startup in HttpApi.Host; migration/seeding in DbMigrator.
- Review `DependsOn` module dependencies as well as project and package references. Use configuration hooks for service registration/options and application initialization hooks for startup work. Confirm the exact APIs for the selected release.
- Obtain queryables through supported async repository APIs such as `GetQueryableAsync`; execute queries with the selected provider's async methods or the framework's query executor. Do not fix a translation error by materializing an unbounded query without documenting its cost and semantics.
- Integrate framework Identity/TenantManagement entities and extensions using the target APIs. Check constructors, manager methods, protected setters, extra properties, indexes, and principal keys instead of replacing type names mechanically.
- Validate legacy-to-target identity maps, nullable host tenant values, audit references, and custom foreign keys in both key modes. Never assume every business entity should use Guid.
- Preserve authorization behavior and tenant filters. Generated tests that allow all permissions cannot prove production authorization correctness.
- Review EF migration `Up`, `Down`, snapshots, custom SQL, and data-copy ordering. Keep test database changes explicit and operate on a disposable/restorable copy for migration verification.
- Treat compatibility shims and `ABPTOVOLO_ORIGINAL` blocks as tracked migration debt. A clean build with stubs does not prove working business operations.

## Verify in increasing depth

1. Run rewriter/converter tests and build the tool.
2. Convert a pinned source revision into a separate output, using explicit versions and `--no-stub` for a strict behavior-preserving baseline.
3. Build the converted solution and run its generated tests. Record failures rather than disabling assertions to obtain a green result.
4. Start the host and check Swagger, source endpoint parity, representative success/failure responses, authentication, denied permissions, tenant isolation, and background jobs.
5. Apply migrations on a database copy and check row counts, key maps, foreign keys, custom SQL, encryption compatibility, and repeat execution behavior.
6. Repeat conversion with persisted state and inspect the diff before merging manual target changes. Do not regenerate into a branch containing uncommitted manual work.
7. Report tool tests, converted-project tests, database checks, and business acceptance separately, including anything not run.

## Extend AbpToVolo

Add a focused regression test, modify the relevant mapping/rewriter/step, run affected checks, and keep a bounded change. Escalate broad domain redesign as an open decision; do not silently restructure the application. Consult `ContractsAndIdTests`, `ZeroKeysTests`, `EndpointParityTests`, and `EndToEndTests` for existing coverage.

## Official references

Use documentation and source tags matching the chosen target release. The `latest` links below are navigation only:

- [Modularity](https://abp.io/docs/latest/framework/architecture/modularity/basics).
- [Repositories](https://abp.io/docs/latest/framework/architecture/domain-driven-design/repositories).
- [ABP Framework source](https://github.com/abpframework/abp).

These are repository skill instructions, not an installed personal skill or proof of migration readiness.
