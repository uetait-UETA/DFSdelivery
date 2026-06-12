-- ============================================================
--  CreaFacturasVentasSAP — Script de configuración BD
--  Base de datos : SMM_DFC (servidor interno)
--  Generado      : 2026-06-09
-- ============================================================
--  INSTRUCCIONES:
--  1. Ejecutar en el servidor INTERNO (smm_dfc @ 10.15.10.90).
--  2. El script es idempotente: puede ejecutarse varias veces
--     sin generar errores ni duplicar datos.
-- ============================================================

USE [SMM_DFC]
GO

-- ══════════════════════════════════════════════════════════════
-- 1.  la_store_sales — Agregar columna DeliveryDocEntry
--     Guarda el DocEntry del ODLN/ORDN SAP (necesario para
--     referenciar las líneas al crear la factura OINV).
-- ══════════════════════════════════════════════════════════════

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME  = 'la_store_sales'
      AND COLUMN_NAME = 'DeliveryDocEntry'
      AND TABLE_SCHEMA = 'dbo'
)
BEGIN
    ALTER TABLE [dbo].[la_store_sales]
        ADD [DeliveryDocEntry] BIGINT NULL;

    PRINT '>>> Columna DeliveryDocEntry agregada a la_store_sales.';
END
ELSE
    PRINT '--- DeliveryDocEntry ya existe en la_store_sales (sin cambios).';
GO

-- ══════════════════════════════════════════════════════════════
-- 2.  la_store_payments — Pagos/cobros del POS
--     Extraídos de POS_TransactionTender, vinculados por transnum.
-- ══════════════════════════════════════════════════════════════

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.TABLES
    WHERE TABLE_NAME  = 'la_store_payments'
      AND TABLE_SCHEMA = 'dbo'
)
BEGIN
    CREATE TABLE [dbo].[la_store_payments] (
        ID              BIGINT IDENTITY(1,1) PRIMARY KEY,
        CompanyId       INT            NOT NULL,
        transnum        VARCHAR(50)    NOT NULL,
        TransactionID   BIGINT         NOT NULL,
        TenderID        NVARCHAR(100)  NULL,        -- "CASH USD-1", "Visa Offline-15"
        Amount          DECIMAL(18,4)  NULL,
        CurrencyID      NVARCHAR(10)   NULL,
        LineType        NVARCHAR(20)   NULL,        -- TenderMerch | TenderChange
        IsChange        BIT            NOT NULL DEFAULT 0,
        SiteID          NVARCHAR(20)   NULL,
        CashDrawerID    NVARCHAR(20)   NULL,
        BusinessDayDate DATE           NULL,
        date_created    DATETIME       NOT NULL DEFAULT GETDATE()
    );

    CREATE INDEX IX_la_store_payments_transnum
        ON [dbo].[la_store_payments] (transnum);

    CREATE INDEX IX_la_store_payments_fecha
        ON [dbo].[la_store_payments] (BusinessDayDate);

    PRINT '>>> Tabla la_store_payments creada.';
END
ELSE
    PRINT '--- la_store_payments ya existe (sin cambios).';
GO

-- ══════════════════════════════════════════════════════════════
-- 3.  ADR_TENDER_SAP — Mapeo TenderID → cuenta SAP B1
--     Define cómo cada método de pago del POS se registra
--     en SAP al crear el Incoming Payment (ORCT).
--
--     PaymentType: 'Cash' | 'CreditCard' | 'Check' | 'Transfer'
--     SapAccount : código de cuenta GL en SAP B1
--
--     Llena esta tabla con los valores reales de tu entorno.
-- ══════════════════════════════════════════════════════════════

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.TABLES
    WHERE TABLE_NAME  = 'ADR_TENDER_SAP'
      AND TABLE_SCHEMA = 'dbo'
)
BEGIN
    CREATE TABLE [dbo].[ADR_TENDER_SAP] (
        ID          INT IDENTITY(1,1) PRIMARY KEY,
        TenderID    NVARCHAR(100)  NOT NULL,    -- valor exacto del POS
        SapAccount  NVARCHAR(20)   NOT NULL,    -- cuenta GL en SAP B1
        PaymentType NVARCHAR(20)   NOT NULL,    -- Cash | CreditCard | Check | Transfer
        Description NVARCHAR(100)  NULL,
        CONSTRAINT UQ_ADR_TENDER_SAP UNIQUE (TenderID)
    );

    -- Ejemplos de registros (descomenta y ajusta con tus valores reales):
    -- INSERT INTO [dbo].[ADR_TENDER_SAP] (TenderID, SapAccount, PaymentType, Description)
    -- VALUES
    --     ('CASH USD-1',      '10100001', 'Cash',       'Efectivo USD'),
    --     ('Visa Offline-15', '10100010', 'CreditCard', 'Tarjeta Visa'),
    --     ('Master-15',       '10100011', 'CreditCard', 'Tarjeta MasterCard');

    PRINT '>>> Tabla ADR_TENDER_SAP creada. RECUERDA insertar los mapeos de TenderID.';
END
ELSE
    PRINT '--- ADR_TENDER_SAP ya existe (sin cambios).';
GO

-- ══════════════════════════════════════════════════════════════
-- 4.  la_daily_invoices — Control de facturas diarias
--     Rastrea qué facturas SAP se han creado por
--     (CardCode, FechaDoc, TransType).
--
--     InvoiceDocNum:
--       NULL  = pendiente (no creada aún)
--       -1    = error (se reintenta en la siguiente ejecución)
--       > 0   = DocNum SAP de la factura creada
-- ══════════════════════════════════════════════════════════════

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.TABLES
    WHERE TABLE_NAME  = 'la_daily_invoices'
      AND TABLE_SCHEMA = 'dbo'
)
BEGIN
    CREATE TABLE [dbo].[la_daily_invoices] (
        ID              BIGINT IDENTITY(1,1) PRIMARY KEY,
        CompanyId       INT            NOT NULL,
        CardCode        NVARCHAR(20)   NOT NULL,
        BPLId           INT            NOT NULL,
        FechaDoc        DATE           NOT NULL,
        TransType       NVARCHAR(5)    NOT NULL,    -- 'DF' | 'DP'
        InvoiceDocNum   BIGINT         NULL,         -- NULL | -1 | >0
        InvoiceDocEntry BIGINT         NULL,         -- DocEntry SAP (para ORCT)
        PaymentDocNum   BIGINT         NULL,         -- DocNum del Incoming Payment
        date_created    DATETIME       NOT NULL DEFAULT GETDATE(),
        date_updated    DATETIME       NULL,
        error_message   NVARCHAR(MAX)  NULL,
        CONSTRAINT UQ_daily_invoice UNIQUE (CardCode, FechaDoc, TransType)
    );

    CREATE INDEX IX_la_daily_invoices_fecha
        ON [dbo].[la_daily_invoices] (FechaDoc, TransType);

    PRINT '>>> Tabla la_daily_invoices creada.';
END
ELSE
    PRINT '--- la_daily_invoices ya existe (sin cambios).';
GO

-- ══════════════════════════════════════════════════════════════
-- 5.  Verificación final
-- ══════════════════════════════════════════════════════════════

PRINT '';
PRINT '======= VERIFICACIÓN =======';
PRINT '';

PRINT '--- DeliveryDocEntry en la_store_sales ---';
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'la_store_sales' AND COLUMN_NAME = 'DeliveryDocEntry';

PRINT '';
PRINT '--- Tablas nuevas ---';
SELECT TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME IN ('la_store_payments','ADR_TENDER_SAP','la_daily_invoices')
  AND TABLE_SCHEMA = 'dbo'
ORDER BY TABLE_NAME;

PRINT '';
PRINT '======= FIN DEL SCRIPT =======';
GO
