-- ============================================================
-- migration_dutytype.sql
-- Agrega la columna DutyType a la_store_sales.
-- Valores: 'DF' (Duty Free) | 'DP' (Duty Paid)
-- ============================================================

ALTER TABLE [dbo].[la_store_sales]
ADD [DutyType] VARCHAR(2) NULL;
GO

-- Paso 1: Rellena desde la vista POS usando el DutyType REAL de cada item.
-- El JOIN es por (CashDrawerID=NUMSERIE, SecuenciaTransaccion=NUMALBARAN, SaleLineNumber=itemnum).
-- Normalizacion: 'DutyFree' -> 'DF' | 'DutyPaid' -> 'DP'
UPDATE s
SET    s.[DutyType] = CASE
                        WHEN pos.DutyType = 'DutyFree' THEN 'DF'
                        ELSE 'DP'
                      END
FROM   [dbo].[la_store_sales] s
INNER JOIN DFSPOS.POS.[dbo].[vw_POS_VentaItems] pos
    ON  pos.CashDrawerID         = s.NUMSERIE
    AND pos.SecuenciaTransaccion = s.NUMALBARAN
    AND pos.SaleLineNumber       = s.itemnum
WHERE  s.[DutyType] IS NULL;
GO

-- Paso 2: Fallback para registros historicos sin coincidencia en la vista POS.
-- Para estos, el tipo del item se desconoce; se usa TransType como aproximacion.
UPDATE [dbo].[la_store_sales]
SET    [DutyType] = ISNULL([TRANSTYPE], 'DF')
WHERE  [DutyType] IS NULL;
GO
