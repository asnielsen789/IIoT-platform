# ADR-003: Choice of working method

## Status

Accepted

## Context

A solo project developed iteratively. It needs a lightweight agile method that provides structure without ceremony overhead. The roadmap has three feature-based iterations with milestones.

## Options

1. **Personal Kanban** — Purely flow-based, no fixed timeboxes, WIP limit.
2. **Scrum-light (timeboxed)** — Fixed two-week sprints with planning and retrospective.
3. **Kanban with milestones (hybrid)** — Kanban for daily work, roadmap iterations as checkpoints.

## Decision

Kanban with milestones.

## Rationale

- Flow-based daily work avoids artificial sprints, which are pure overhead for a solo developer.
- A WIP limit of two reduces context switching.
- Roadmap milestones provide natural demo points.
- A retrospective at each milestone drives process improvement without sprint ceremony.

## Consequences

- No fixed cadence, which requires self-discipline to maintain momentum.
- Demo and review happen at milestones rather than at a fixed interval.
- Tooling: GitHub Projects (Kanban board), GitHub Issues (tasks), Milestones (iterations).
