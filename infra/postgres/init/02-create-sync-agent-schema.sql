CREATE SCHEMA IF NOT EXISTS sync_agent;

CREATE TABLE IF NOT EXISTS sync_agent.outbox_events (
    event_id uuid PRIMARY KEY,
    source_instance_id text NOT NULL,
    source_system text NOT NULL,
    entity_type text NOT NULL,
    entity_key text NOT NULL,
    event_type text NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    captured_at_utc timestamptz NOT NULL,
    schema_version text NOT NULL,
    payload jsonb NOT NULL,
    payload_hash text NOT NULL,
    trace_id text NULL,
    status text NOT NULL DEFAULT 'pending',
    attempt_count integer NOT NULL DEFAULT 0,
    next_attempt_at_utc timestamptz NOT NULL DEFAULT now(),
    last_error text NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT outbox_events_source_system_check
        CHECK (source_system IN ('arpa', 'pdv_local')),
    CONSTRAINT outbox_events_entity_type_check
        CHECK (entity_type IN ('cliente', 'produto', 'estoque', 'venda', 'financeiro')),
    CONSTRAINT outbox_events_event_type_check
        CHECK (event_type IN ('upsert', 'delete_logico', 'status_update')),
    CONSTRAINT outbox_events_schema_version_check
        CHECK (schema_version ~ '^v[0-9]+(\.[0-9]+)?$'),
    CONSTRAINT outbox_events_status_check
        CHECK (status IN ('pending', 'in_flight', 'accepted', 'rejected', 'dead_letter'))
);

DO $$
BEGIN
    ALTER TABLE sync_agent.outbox_events
        DROP CONSTRAINT IF EXISTS outbox_events_source_system_check;

    ALTER TABLE sync_agent.outbox_events
        ADD CONSTRAINT outbox_events_source_system_check
        CHECK (source_system IN ('arpa', 'pdv_local'));
END $$;

DO $$
BEGIN
    ALTER TABLE sync_agent.outbox_events
        ADD CONSTRAINT outbox_events_entity_type_check
        CHECK (entity_type IN ('cliente', 'produto', 'estoque', 'venda', 'financeiro'));
EXCEPTION
    WHEN duplicate_object THEN NULL;
END $$;

DO $$
BEGIN
    ALTER TABLE sync_agent.outbox_events
        ADD CONSTRAINT outbox_events_event_type_check
        CHECK (event_type IN ('upsert', 'delete_logico', 'status_update'));
EXCEPTION
    WHEN duplicate_object THEN NULL;
END $$;

DO $$
BEGIN
    ALTER TABLE sync_agent.outbox_events
        ADD CONSTRAINT outbox_events_schema_version_check
        CHECK (schema_version ~ '^v[0-9]+(\.[0-9]+)?$');
EXCEPTION
    WHEN duplicate_object THEN NULL;
END $$;

CREATE INDEX IF NOT EXISTS ix_outbox_events_pending
    ON sync_agent.outbox_events (status, next_attempt_at_utc, captured_at_utc);

CREATE INDEX IF NOT EXISTS ix_outbox_events_entity
    ON sync_agent.outbox_events (entity_type, entity_key);

CREATE TABLE IF NOT EXISTS sync_agent.dispatch_attempts (
    attempt_id uuid PRIMARY KEY,
    batch_id uuid NOT NULL,
    event_id uuid NOT NULL REFERENCES sync_agent.outbox_events (event_id),
    attempt_number integer NOT NULL,
    status text NOT NULL,
    response_status_code integer NULL,
    response_body jsonb NULL,
    error_classification text NULL,
    error_message text NULL,
    attempted_at_utc timestamptz NOT NULL DEFAULT now(),
    completed_at_utc timestamptz NULL,
    CONSTRAINT dispatch_attempts_status_check
        CHECK (status IN ('started', 'accepted', 'rejected', 'transient_error', 'permanent_error'))
);

CREATE INDEX IF NOT EXISTS ix_dispatch_attempts_event
    ON sync_agent.dispatch_attempts (event_id, attempted_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_dispatch_attempts_batch
    ON sync_agent.dispatch_attempts (batch_id);

CREATE TABLE IF NOT EXISTS sync_agent.inbox_events (
    event_id uuid PRIMARY KEY,
    source_instance_id text NOT NULL,
    received_at_utc timestamptz NOT NULL DEFAULT now(),
    processed_at_utc timestamptz NULL,
    schema_version text NOT NULL,
    payload jsonb NOT NULL,
    payload_hash text NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_inbox_events_processed
    ON sync_agent.inbox_events (processed_at_utc, received_at_utc);

CREATE TABLE IF NOT EXISTS sync_agent.agent_state (
    state_key text PRIMARY KEY,
    state_value jsonb NOT NULL,
    updated_at_utc timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS sync_agent.reconciliation_runs (
    reconciliation_id uuid PRIMARY KEY,
    entity_type text NULL,
    window_start_utc timestamptz NOT NULL,
    window_end_utc timestamptz NOT NULL,
    status text NOT NULL,
    summary jsonb NOT NULL DEFAULT '{}'::jsonb,
    started_at_utc timestamptz NOT NULL DEFAULT now(),
    completed_at_utc timestamptz NULL,
    CONSTRAINT reconciliation_runs_status_check
        CHECK (status IN ('started', 'completed', 'failed'))
);

CREATE INDEX IF NOT EXISTS ix_reconciliation_runs_window
    ON sync_agent.reconciliation_runs (window_start_utc, window_end_utc);

CREATE TABLE IF NOT EXISTS sync_agent.dead_letter_events (
    event_id uuid PRIMARY KEY,
    source_instance_id text NOT NULL,
    source_system text NOT NULL DEFAULT 'arpa',
    entity_type text NOT NULL,
    entity_key text NOT NULL,
    event_type text NOT NULL,
    occurred_at_utc timestamptz NOT NULL DEFAULT now(),
    captured_at_utc timestamptz NOT NULL DEFAULT now(),
    schema_version text NOT NULL,
    payload jsonb NOT NULL,
    payload_hash text NOT NULL,
    trace_id text NULL,
    attempt_count integer NOT NULL DEFAULT 0,
    reason text NOT NULL,
    failed_at_utc timestamptz NOT NULL DEFAULT now(),
    retained_until_utc timestamptz NOT NULL DEFAULT now() + interval '90 days'
);

ALTER TABLE sync_agent.dead_letter_events
    ADD COLUMN IF NOT EXISTS source_system text NOT NULL DEFAULT 'arpa';

ALTER TABLE sync_agent.dead_letter_events
    ADD COLUMN IF NOT EXISTS occurred_at_utc timestamptz NOT NULL DEFAULT now();

ALTER TABLE sync_agent.dead_letter_events
    ADD COLUMN IF NOT EXISTS captured_at_utc timestamptz NOT NULL DEFAULT now();

ALTER TABLE sync_agent.dead_letter_events
    ADD COLUMN IF NOT EXISTS trace_id text NULL;

ALTER TABLE sync_agent.dead_letter_events
    ADD COLUMN IF NOT EXISTS attempt_count integer NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS ix_dead_letter_events_retention
    ON sync_agent.dead_letter_events (retained_until_utc);
