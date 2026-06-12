-- ============================================================
-- fix_dutytype.sql
-- Corrige los registros de la_store_sales donde DutyType fue
-- asignado incorrectamente con el valor de TRANSTYPE en lugar
-- del DutyType real del item.
--
-- Ejecutar UNA SOLA VEZ sobre smm_dfc despues de haber corrido
-- migration_dutytype.sql con el UPDATE incorrecto.
-- ============================================================

-- Actualiza TODOS los registros a partir del DutyType real del item en la vista POS.
-- No filtra por DeliveryDocNum para corregir tanto pendientes como ya procesados.
UPDATE s
SET    s.[DutyType] = CASE
                        WHEN pos.DutyType = 'DutyFree' THEN 'DF'
                        ELSE 'DP'
                      END
FROM   [dbo].[la_store_sales] s
INNER JOIN DFSPOS.POS.[dbo].[vw_POS_VentaItems] pos
    ON  pos.CashDrawerID         = s.NUMSERIE
    AND pos.SecuenciaTransaccion = s.NUMALBARAN
    AND pos.SaleLineNumber       = s.itemnum;
GO

-- Verificacion: muestra cuantos registros quedaron con cada DutyType
-- y si algun registro no tuvo coincidencia en la vista POS (quedaria con valor anterior).
SELECT
    DutyType,
    TRANSTYPE,
    COUNT(*) AS Total
FROM [dbo].[la_store_sales]
GROUP BY DutyType, TRANSTYPE
ORDER BY TRANSTYPE, DutyType;
GO
