-- ============================================================
-- migration_salestaxamount.sql
-- Agrega la columna SalesTaxAmount a la_store_sales.
-- Almacena el impuesto exacto de la transaccion (TransactionGrandAmount - TransactionNetAmount)
-- leido de vw_POS_VentaItems en el momento de la extraccion (Fase 1).
-- ============================================================

ALTER TABLE [dbo].[la_store_sales]
ADD [SalesTaxAmount] DECIMAL(18, 4) NULL;
GO

-- Backfill desde la vista POS para registros existentes.
-- Usa el primer valor distinto por (NUMSERIE, NUMALBARAN) ya que es un campo de nivel transaccion.
UPDATE s
SET    s.[SalesTaxAmount] = pos.TaxAmount
FROM   [dbo].[la_store_sales] s
INNER JOIN (
    SELECT DISTINCT
        CashDrawerID,
        SecuenciaTransaccion,
        ISNULL(TransactionGrandAmount - TransactionNetAmount, 0) AS TaxAmount
    FROM DFSPOS.POS.[dbo].[vw_POS_VentaItems]
) pos
    ON  pos.CashDrawerID         = s.NUMSERIE
    AND pos.SecuenciaTransaccion = s.NUMALBARAN
WHERE s.[SalesTaxAmount] IS NULL;
GO

-- Fallback: registros historicos sin coincidencia en la vista POS quedan en 0
UPDATE [dbo].[la_store_sales]
SET    [SalesTaxAmount] = 0
WHERE  [SalesTaxAmount] IS NULL;
GO
