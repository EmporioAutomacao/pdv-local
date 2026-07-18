CREATE SCHEMA IF NOT EXISTS pdv;

CREATE TABLE IF NOT EXISTS pdv.operators (
    operator_id uuid PRIMARY KEY,
    external_operator_id text NULL,
    login text NOT NULL UNIQUE,
    display_name text NOT NULL,
    password_hash text NOT NULL,
    role text NOT NULL DEFAULT 'operator',
    active boolean NOT NULL DEFAULT true,
    permissions jsonb NOT NULL DEFAULT '[]'::jsonb,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE pdv.operators
    ADD COLUMN IF NOT EXISTS external_operator_id text NULL;

ALTER TABLE pdv.operators
    ADD COLUMN IF NOT EXISTS permissions jsonb NOT NULL DEFAULT '[]'::jsonb;

CREATE UNIQUE INDEX IF NOT EXISTS ux_operators_external_operator_id
    ON pdv.operators (external_operator_id)
    WHERE external_operator_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS pdv.cash_sessions (
    cash_session_id uuid PRIMARY KEY,
    operator_id uuid NOT NULL REFERENCES pdv.operators (operator_id),
    status text NOT NULL,
    opened_at_utc timestamptz NOT NULL,
    closed_at_utc timestamptz NULL,
    opening_amount numeric(14, 2) NOT NULL DEFAULT 0,
    closing_amount numeric(14, 2) NULL,
    notes text NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT cash_sessions_status_check CHECK (status IN ('open', 'closed', 'cancelled'))
);

CREATE INDEX IF NOT EXISTS ix_cash_sessions_status
    ON pdv.cash_sessions (status, opened_at_utc DESC);

CREATE TABLE IF NOT EXISTS pdv.cash_movements (
    movement_id uuid PRIMARY KEY,
    cash_session_id uuid NOT NULL REFERENCES pdv.cash_sessions (cash_session_id),
    operator_id uuid NOT NULL REFERENCES pdv.operators (operator_id),
    supervisor_operator_id uuid NOT NULL REFERENCES pdv.operators (operator_id),
    movement_type text NOT NULL,
    amount numeric(14, 2) NOT NULL,
    reason text NOT NULL,
    occurred_at_utc timestamptz NOT NULL DEFAULT now(),
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT cash_movements_type_check CHECK (movement_type IN ('supply', 'withdrawal')),
    CONSTRAINT cash_movements_amount_check CHECK (amount > 0)
);

CREATE INDEX IF NOT EXISTS ix_cash_movements_session
    ON pdv.cash_movements (cash_session_id, occurred_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_cash_movements_type
    ON pdv.cash_movements (movement_type, occurred_at_utc DESC);

CREATE TABLE IF NOT EXISTS pdv.products (
    product_id uuid PRIMARY KEY,
    source_system text NOT NULL,
    external_key text NOT NULL,
    sku text NULL,
    name text NOT NULL,
    barcode text NULL,
    unit text NOT NULL DEFAULT 'UN',
    price numeric(14, 4) NOT NULL DEFAULT 0,
    active boolean NOT NULL DEFAULT true,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    UNIQUE (source_system, external_key)
);

CREATE INDEX IF NOT EXISTS ix_products_barcode
    ON pdv.products (barcode)
    WHERE barcode IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_products_name
    ON pdv.products (name);

CREATE TABLE IF NOT EXISTS pdv.payment_species (
    payment_species_id uuid PRIMARY KEY,
    source_system text NOT NULL,
    external_key text NOT NULL,
    name text NOT NULL,
    kind text NOT NULL DEFAULT 'other',
    requires_tef boolean NOT NULL DEFAULT false,
    allows_change boolean NOT NULL DEFAULT false,
    active boolean NOT NULL DEFAULT true,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    UNIQUE (source_system, external_key),
    CONSTRAINT payment_species_kind_check CHECK (kind IN ('cash', 'card', 'pix', 'voucher', 'credit', 'other'))
);

CREATE INDEX IF NOT EXISTS ix_payment_species_active
    ON pdv.payment_species (active, name);

CREATE TABLE IF NOT EXISTS pdv.payment_conditions (
    payment_condition_id uuid PRIMARY KEY,
    source_system text NOT NULL,
    external_key text NOT NULL,
    name text NOT NULL,
    installments integer NOT NULL DEFAULT 1,
    first_due_days integer NOT NULL DEFAULT 0,
    interval_days integer NOT NULL DEFAULT 0,
    active boolean NOT NULL DEFAULT true,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    UNIQUE (source_system, external_key),
    CONSTRAINT payment_conditions_installments_check CHECK (installments >= 1),
    CONSTRAINT payment_conditions_first_due_days_check CHECK (first_due_days >= 0),
    CONSTRAINT payment_conditions_interval_days_check CHECK (interval_days >= 0)
);

CREATE INDEX IF NOT EXISTS ix_payment_conditions_active
    ON pdv.payment_conditions (active, name);

CREATE TABLE IF NOT EXISTS pdv.customers (
    customer_id uuid PRIMARY KEY,
    source_system text NOT NULL,
    external_key text NOT NULL,
    document text NULL,
    name text NOT NULL,
    email text NULL,
    phone text NULL,
    active boolean NOT NULL DEFAULT true,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    UNIQUE (source_system, external_key)
);

CREATE INDEX IF NOT EXISTS ix_customers_document
    ON pdv.customers (document)
    WHERE document IS NOT NULL;

CREATE TABLE IF NOT EXISTS pdv.sales (
    sale_id uuid PRIMARY KEY,
    cash_session_id uuid NOT NULL REFERENCES pdv.cash_sessions (cash_session_id),
    operator_id uuid NOT NULL REFERENCES pdv.operators (operator_id),
    customer_id uuid NULL REFERENCES pdv.customers (customer_id),
    customer_document text NULL,
    sale_number text NOT NULL UNIQUE,
    status text NOT NULL,
    subtotal_amount numeric(14, 2) NOT NULL DEFAULT 0,
    discount_amount numeric(14, 2) NOT NULL DEFAULT 0,
    total_amount numeric(14, 2) NOT NULL DEFAULT 0,
    completed_at_utc timestamptz NULL,
    cancelled_at_utc timestamptz NULL,
    sync_status text NOT NULL DEFAULT 'not_published',
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sales_status_check CHECK (status IN ('draft', 'completed', 'cancelled')),
    CONSTRAINT sales_sync_status_check CHECK (sync_status IN ('not_published', 'pending_sync', 'sent', 'accepted', 'rejected'))
);

-- Bases existentes (criadas antes do campo de CPF/CNPJ na venda)
ALTER TABLE pdv.sales ADD COLUMN IF NOT EXISTS customer_document text NULL;

CREATE INDEX IF NOT EXISTS ix_sales_status
    ON pdv.sales (status, created_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_sales_sync_status
    ON pdv.sales (sync_status, updated_at_utc DESC);

CREATE TABLE IF NOT EXISTS pdv.sale_items (
    sale_item_id uuid PRIMARY KEY,
    sale_id uuid NOT NULL REFERENCES pdv.sales (sale_id) ON DELETE CASCADE,
    product_id uuid NOT NULL REFERENCES pdv.products (product_id),
    line_number integer NOT NULL,
    quantity numeric(14, 4) NOT NULL,
    unit_price numeric(14, 4) NOT NULL,
    discount_amount numeric(14, 2) NOT NULL DEFAULT 0,
    total_amount numeric(14, 2) NOT NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    UNIQUE (sale_id, line_number)
);

CREATE TABLE IF NOT EXISTS pdv.payments (
    payment_id uuid PRIMARY KEY,
    sale_id uuid NOT NULL REFERENCES pdv.sales (sale_id) ON DELETE CASCADE,
    payment_method text NOT NULL,
    amount numeric(14, 2) NOT NULL,
    status text NOT NULL DEFAULT 'captured',
    authorization_code text NULL,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT payments_status_check CHECK (status IN ('pending', 'captured', 'cancelled', 'failed'))
);

CREATE INDEX IF NOT EXISTS ix_payments_sale
    ON pdv.payments (sale_id);

CREATE TABLE IF NOT EXISTS pdv.operation_audits (
    audit_id uuid PRIMARY KEY,
    operation_type text NOT NULL,
    cash_session_id uuid NULL REFERENCES pdv.cash_sessions (cash_session_id),
    sale_id uuid NULL REFERENCES pdv.sales (sale_id) ON DELETE SET NULL,
    operator_id uuid NOT NULL REFERENCES pdv.operators (operator_id),
    supervisor_operator_id uuid NOT NULL REFERENCES pdv.operators (operator_id),
    reason text NULL,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at_utc timestamptz NOT NULL DEFAULT now(),
    created_at_utc timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_operation_audits_occurred_at
    ON pdv.operation_audits (occurred_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_operation_audits_operation_type
    ON pdv.operation_audits (operation_type, occurred_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_operation_audits_sale
    ON pdv.operation_audits (sale_id)
    WHERE sale_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS pdv.settings (
    setting_key text PRIMARY KEY,
    setting_value jsonb NOT NULL,
    updated_at_utc timestamptz NOT NULL DEFAULT now()
);
