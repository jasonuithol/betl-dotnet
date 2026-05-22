-- betl-dotnet full-coverage — postgres-side schema + seed rows.
-- Run with: psql -h localhost -p 5433 -U postgres -d betl_coverage -f schemas.postgres.sql
-- (Create the database first via `createdb -h localhost -p 5433 -U postgres betl_coverage`.)

CREATE SCHEMA IF NOT EXISTS staging;
CREATE SCHEMA IF NOT EXISTS warehouse;
CREATE SCHEMA IF NOT EXISTS audit;

DROP TABLE IF EXISTS audit.run_log              CASCADE;
DROP TABLE IF EXISTS warehouse.vendor_metrics   CASCADE;
DROP TABLE IF EXISTS warehouse.analytics_daily  CASCADE;
DROP TABLE IF EXISTS warehouse.daily_orders     CASCADE;
DROP TABLE IF EXISTS warehouse.product_categories CASCADE;
DROP TABLE IF EXISTS warehouse.customers        CASCADE;
DROP TABLE IF EXISTS staging.daily_orders       CASCADE;
DROP TABLE IF EXISTS staging.gen_scratch        CASCADE;

CREATE TABLE warehouse.customers (
    customer_id     INTEGER PRIMARY KEY,
    name            TEXT NOT NULL,
    email           TEXT NOT NULL,
    signup_date     DATE NOT NULL,
    vip             BOOLEAN NOT NULL DEFAULT FALSE,
    lifetime_spend  NUMERIC(12,2) NOT NULL DEFAULT 0,
    fingerprint     BYTEA,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE staging.daily_orders (
    order_id        BIGINT NOT NULL,
    customer_id     INTEGER NOT NULL,
    sku             TEXT NOT NULL,
    qty             INTEGER NOT NULL,
    unit_price      NUMERIC(10,2) NOT NULL,
    ordered_at      TIMESTAMPTZ NOT NULL,
    source          TEXT
);

CREATE TABLE warehouse.daily_orders (
    order_id        BIGINT PRIMARY KEY,
    customer_id     INTEGER NOT NULL,
    sku             TEXT NOT NULL,
    qty             INTEGER NOT NULL,
    unit_price      NUMERIC(10,2) NOT NULL,
    line_total      NUMERIC(12,2) NOT NULL,
    ordered_at      TIMESTAMPTZ NOT NULL,
    is_bulk         BOOLEAN NOT NULL DEFAULT FALSE,
    source          TEXT,
    loaded_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE warehouse.analytics_daily (
    load_date       DATE NOT NULL,
    sku             TEXT NOT NULL,
    qty_total       INTEGER NOT NULL,
    revenue         NUMERIC(14,2) NOT NULL,
    PRIMARY KEY (load_date, sku)
);

CREATE TABLE warehouse.vendor_metrics (
    sku             TEXT NOT NULL,
    vendor          TEXT NOT NULL,
    rating          INTEGER NOT NULL,
    last_review     TIMESTAMP NOT NULL,
    PRIMARY KEY (sku, vendor)
);

CREATE TABLE audit.run_log (
    run_id          TEXT NOT NULL,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    stage           TEXT NOT NULL,
    rows_in         BIGINT,
    rows_out        BIGINT,
    note            TEXT
);

CREATE TABLE warehouse.product_categories (
    sku             TEXT PRIMARY KEY,
    category        TEXT NOT NULL,
    seasonal        BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE staging.gen_scratch (
    seq             BIGINT NOT NULL,
    payload         TEXT NOT NULL
);

-- Seed rows.
INSERT INTO warehouse.customers (customer_id, name, email, signup_date, vip, lifetime_spend, fingerprint) VALUES
  (1001, 'Alice Anderson',  'alice@example.com',  DATE '2024-03-01', TRUE,  4280.50, decode('a1b2c3','hex')),
  (1002, 'Bob Brown',       'bob@example.com',    DATE '2024-06-12', FALSE,  315.20, decode('deadbeef','hex')),
  (1003, 'Carla Chen',      'carla@example.com',  DATE '2025-01-08', TRUE,  9120.00, decode('cafebabe','hex')),
  (1004, 'Dan Davies',      'dan@example.com',    DATE '2025-09-19', FALSE,   45.99, decode('00ff00','hex')),
  (1005, 'Eva Espinoza',    'eva@example.com',    DATE '2026-02-04', FALSE,    0.00, decode('11223344','hex'));

INSERT INTO warehouse.product_categories (sku, category, seasonal) VALUES
  ('SKU-001', 'electronics', FALSE),
  ('SKU-002', 'apparel',     TRUE),
  ('SKU-003', 'home',        FALSE),
  ('SKU-004', 'apparel',     TRUE),
  ('SKU-005', 'grocery',     FALSE);
