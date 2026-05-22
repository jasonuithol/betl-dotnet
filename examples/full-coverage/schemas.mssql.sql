-- betl-dotnet full-coverage — mssql-side schema + seed rows.
-- Run with: sqlcmd -S localhost\SQLEXPRESS -E -d betl_coverage -i schemas.mssql.sql
-- (Create the database first via `CREATE DATABASE betl_coverage` from master.)

IF OBJECT_ID('dbo.run_summary')      IS NOT NULL DROP TABLE dbo.run_summary;
IF OBJECT_ID('dbo.vendor_directory') IS NOT NULL DROP TABLE dbo.vendor_directory;
IF OBJECT_ID('dbo.order_bulk_stage') IS NOT NULL DROP TABLE dbo.order_bulk_stage;
IF OBJECT_ID('dbo.order_facts')      IS NOT NULL DROP TABLE dbo.order_facts;
IF OBJECT_ID('dbo.products')         IS NOT NULL DROP TABLE dbo.products;

CREATE TABLE dbo.products (
    sku             NVARCHAR(32) NOT NULL PRIMARY KEY,
    name            NVARCHAR(120) NOT NULL,
    price           DECIMAL(10,2) NOT NULL,
    category        NVARCHAR(40) NOT NULL,
    on_hand         INT NOT NULL DEFAULT 0,
    updated_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.order_facts (
    order_id        BIGINT NOT NULL PRIMARY KEY,
    customer_id     INT NOT NULL,
    sku             NVARCHAR(32) NOT NULL,
    qty             INT NOT NULL,
    line_total      DECIMAL(12,2) NOT NULL,
    order_date      DATE NOT NULL,
    loaded_at       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    quarter         INT NULL
);

CREATE TABLE dbo.order_bulk_stage (
    order_id        BIGINT NOT NULL,
    customer_id     INT NOT NULL,
    sku             NVARCHAR(32) NOT NULL,
    qty             INT NOT NULL,
    line_total      DECIMAL(12,2) NOT NULL,
    order_date      DATE NOT NULL
);

CREATE TABLE dbo.vendor_directory (
    vendor_code     NVARCHAR(16) NOT NULL PRIMARY KEY,
    vendor_name     NVARCHAR(120) NOT NULL,
    region          NVARCHAR(8) NOT NULL
);

CREATE TABLE dbo.run_summary (
    run_id          UNIQUEIDENTIFIER NOT NULL,
    completed_at    DATETIME2 NOT NULL,
    stage_count     INT NOT NULL,
    note            NVARCHAR(400)
);

-- Seed rows.
INSERT INTO dbo.products (sku, name, price, category, on_hand) VALUES
  (N'SKU-001', N'USB-C Charger 65W', 39.99, N'electronics', 120),
  (N'SKU-002', N'Wool Beanie',       24.50, N'apparel',      80),
  (N'SKU-003', N'Ceramic Mug',        9.95, N'home',        300),
  (N'SKU-004', N'Cotton Tee',        18.00, N'apparel',     200),
  (N'SKU-005', N'Coffee Beans 1kg',  22.40, N'grocery',      60);

INSERT INTO dbo.vendor_directory (vendor_code, vendor_name, region) VALUES
  (N'V-EU-01', N'EuroSupply BV',    N'EU'),
  (N'V-NA-02', N'PacificGoods Inc', N'NA'),
  (N'V-AP-03', N'AsiaPort Ltd',     N'AP');
