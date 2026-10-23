# Architecture

This repository follows a clean architecture model aligned with the CloudEntitle CIEM project brief.

## Layers

- Domain: core entities and business rules
- Application: orchestrations, use cases, contracts
- Infrastructure: SQLite, logging, configuration, persistence, local storage
- Cloud provider adapters: Azure, AWS, GCP mapping to normalized models
- Desktop UI: WPF + MVVM shell and screens
- Licensing library: retained for future editions; the free standalone desktop does not reference or enforce it

## Principles

- Domain independence from infrastructure
- Read-only cloud discovery in v1
- Provider-agnostic risk evaluation
- Dependency injection across service boundaries
- Async workflows, pagination, and cancellation support
