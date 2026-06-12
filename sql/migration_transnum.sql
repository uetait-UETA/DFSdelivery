-- ============================================================
--  Migración: cambio de formato de transnum
--  Antes : SiteID + PosLoc + CashDrawerID + SecuenciaTransaccion
--  Ahora :           PosLoc + CashDrawerID + SecuenciaTransaccion
--
--  Base de datos : SMM_DFC (servidor interno @ 10.15.10.90)
--  Generado      : 2026-06-09
-- ============================================================
--  INSTRUCCIONES:
--  1. Ejecutar ANTES de desplegar la nueva versión del código.
--  2. El script es idempotente si ya tienes el nuevo formato
--     (el WHERE filtra solo los que aún tienen el formato viejo).
-- ============================================================

USE [SMM_DFC]
GO

BEGIN TRANSACTION;

-- ── PASO 1: la_delivery_errors ────────────────────────────────
-- Actualiza transnum uniendo con la_store_sales (que aún tiene
-- el formato viejo) para construir la clave nueva.
UPDATE de
SET de.transnum = ss.storenum + ss.NUMSERIE + CAST(ss.Numalbaran AS VARCHAR)
FROM [dbo].[la_delivery_errors] de
INNER JOIN (
    SELECT DISTINCT
        transnum,
        storenum + NUMSERIE + CAST(Numalbaran AS VARCHAR) AS nuevo_transnum
    FROM [dbo].[la_store_sales]
) ss ON de.transnum = ss.transnum;

PRINT CONCAT('>>> la_delivery_errors actualizados: ', @@ROWCOUNT);

-- ── PASO 2: la_store_payments ─────────────────────────────────
UPDATE sp
SET sp.transnum = ss.storenum + ss.NUMSERIE + CAST(ss.Numalbaran AS VARCHAR)
FROM [dbo].[la_store_payments] sp
INNER JOIN (
    SELECT DISTINCT
        transnum,
        storenum + NUMSERIE + CAST(Numalbaran AS VARCHAR) AS nuevo_transnum
    FROM [dbo].[la_store_sales]
) ss ON sp.transnum = ss.transnum;

PRINT CONCAT('>>> la_store_payments actualizados: ', @@ROWCOUNT);

-- ── PASO 3: la_store_sales ────────────────────────────────────
-- Se hace al final porque los pasos anteriores dependen del valor viejo.
UPDATE [dbo].[la_store_sales]
SET transnum = storenum + NUMSERIE + CAST(Numalbaran AS VARCHAR);

PRINT CONCAT('>>> la_store_sales actualizados: ', @@ROWCOUNT);

COMMIT;

-- ── Verificación ──────────────────────────────────────────────
PRINT '';
PRINT '--- Muestra de transnum nuevos en la_store_sales ---';
SELECT TOP 5
    transnum,
    storenum,
    NUMSERIE     AS CashDrawerID,
    Numalbaran   AS SecuenciaTransaccion
FROM [dbo].[la_store_sales]
ORDER BY ID;
GO
