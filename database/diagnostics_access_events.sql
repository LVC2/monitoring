USE [apacs3000rus];
GO

/*
    APACS Apollo 3000 diagnostic script.
    READ ONLY: this script only inspects metadata and reads sample rows.
    It does not modify APACS data.

    Goal:
      1) Find the real access/card event journal.
      2) Identify the card/holder reference stored by an access event.
      3) Identify reader/access-point information.
      4) Build the final join to TAPCCARDHOLDERREF -> TAPCCARDHOLDER.
*/

SET NOCOUNT ON;

PRINT '=== 1. Candidate event tables: columns ===';
SELECT
    TABLE_SCHEMA,
    TABLE_NAME,
    ORDINAL_POSITION,
    COLUMN_NAME,
    DATA_TYPE,
    CHARACTER_MAXIMUM_LENGTH
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME IN
(
    'TAPCAUDITEVENT',
    'TAPCEVENTACKRECORD',
    'TAPCLINKEDEVENT',
    'TAPCSYSEVENTSCOMMON',
    'TAPLCIVFUNCEVENTLOG'
)
ORDER BY TABLE_NAME, ORDINAL_POSITION;

PRINT '=== 2. All tables whose names look event/log/journal related ===';
SELECT
    TABLE_SCHEMA,
    TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE = 'BASE TABLE'
  AND
  (
      UPPER(TABLE_NAME) LIKE '%EVENT%'
      OR UPPER(TABLE_NAME) LIKE '%HIST%'
      OR UPPER(TABLE_NAME) LIKE '%LOG%'
      OR UPPER(TABLE_NAME) LIKE '%JOURNAL%'
      OR UPPER(TABLE_NAME) LIKE '%ACCESS%'
  )
ORDER BY TABLE_NAME;

PRINT '=== 3. Card-holder tables: columns ===';
SELECT
    TABLE_SCHEMA,
    TABLE_NAME,
    ORDINAL_POSITION,
    COLUMN_NAME,
    DATA_TYPE,
    CHARACTER_MAXIMUM_LENGTH
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME IN ('TAPCCARDHOLDER', 'TAPCCARDHOLDERREF')
ORDER BY TABLE_NAME, ORDINAL_POSITION;

PRINT '=== 4. Reader/access-point tables: columns ===';
SELECT
    TABLE_SCHEMA,
    TABLE_NAME,
    ORDINAL_POSITION,
    COLUMN_NAME,
    DATA_TYPE,
    CHARACTER_MAXIMUM_LENGTH
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME IN
(
    'TAPCASP4ACCESSPOINT',
    'TAPCASP4READER',
    'TAPCASP4READERWRAP',
    'TAPCVREADER',
    'TAPCVREADERGROUP',
    'TAPCVREADERGROUPELEM',
    'TAPLAIMREADER',
    'TAPLMCREADER',
    'TAPLAPNREADER',
    'TSUPACREADER',
    'TSUPAC2READERRFID',
    'TSUPAC2DOOR'
)
ORDER BY TABLE_NAME, ORDINAL_POSITION;

PRINT '=== 5. Candidate event-table row counts ===';
DECLARE @sql nvarchar(max) = N'';

SELECT @sql = @sql +
    N'SELECT N''' + REPLACE(TABLE_SCHEMA + N'.' + TABLE_NAME, '''', '''''') +
    N''' AS TableName, COUNT_BIG(*) AS RowCount FROM ' +
    QUOTENAME(TABLE_SCHEMA) + N'.' + QUOTENAME(TABLE_NAME) + N' UNION ALL '
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE = 'BASE TABLE'
  AND
  (
      UPPER(TABLE_NAME) LIKE '%EVENT%'
      OR UPPER(TABLE_NAME) LIKE '%HIST%'
      OR UPPER(TABLE_NAME) LIKE '%LOG%'
      OR UPPER(TABLE_NAME) LIKE '%JOURNAL%'
      OR UPPER(TABLE_NAME) LIKE '%ACCESS%'
  );

IF LEN(@sql) > 0
BEGIN
    SET @sql = LEFT(@sql, LEN(@sql) - LEN(N' UNION ALL ')) + N' ORDER BY TableName;';
    EXEC sys.sp_executesql @sql;
END;

PRINT '=== 6. Foreign keys mentioning event/card/reader/holder tables ===';
SELECT
    fk.name AS ForeignKeyName,
    OBJECT_SCHEMA_NAME(fk.parent_object_id) AS ParentSchema,
    OBJECT_NAME(fk.parent_object_id) AS ParentTable,
    pc.name AS ParentColumn,
    OBJECT_SCHEMA_NAME(fk.referenced_object_id) AS ReferencedSchema,
    OBJECT_NAME(fk.referenced_object_id) AS ReferencedTable,
    rc.name AS ReferencedColumn
FROM sys.foreign_key_columns fkc
JOIN sys.foreign_keys fk
  ON fk.object_id = fkc.constraint_object_id
JOIN sys.columns pc
  ON pc.object_id = fkc.parent_object_id
 AND pc.column_id = fkc.parent_column_id
JOIN sys.columns rc
  ON rc.object_id = fkc.referenced_object_id
 AND rc.column_id = fkc.referenced_column_id
WHERE
    UPPER(OBJECT_NAME(fk.parent_object_id)) LIKE '%EVENT%'
    OR UPPER(OBJECT_NAME(fk.parent_object_id)) LIKE '%ACCESS%'
    OR UPPER(OBJECT_NAME(fk.parent_object_id)) LIKE '%READER%'
    OR UPPER(OBJECT_NAME(fk.parent_object_id)) LIKE '%CARD%'
    OR UPPER(OBJECT_NAME(fk.parent_object_id)) LIKE '%HOLDER%'
    OR UPPER(OBJECT_NAME(fk.referenced_object_id)) LIKE '%EVENT%'
    OR UPPER(OBJECT_NAME(fk.referenced_object_id)) LIKE '%ACCESS%'
    OR UPPER(OBJECT_NAME(fk.referenced_object_id)) LIKE '%READER%'
    OR UPPER(OBJECT_NAME(fk.referenced_object_id)) LIKE '%CARD%'
    OR UPPER(OBJECT_NAME(fk.referenced_object_id)) LIKE '%HOLDER%'
ORDER BY ParentTable, ForeignKeyName;

PRINT '=== 7. Current card-holder examples (for later join verification) ===';
SELECT TOP (20)
    h.FID0,
    h.FID1,
    h.FNUMBER,
    h.FFIRSTNAME,
    h.FLASTNAME,
    h.FMIDDLENAME,
    h.FACTIVE,
    h.FSTATUS,
    r.FCARDNUM,
    r.FSACARD0,
    r.FSACARD1,
    r.FSAHOLDER0,
    r.FSAHOLDER1
FROM dbo.TAPCCARDHOLDER h
LEFT JOIN dbo.TAPCCARDHOLDERREF r
    ON r.FSAHOLDER0 = h.FID0
   AND r.FSAHOLDER1 = h.FID1
WHERE h.FACTIVE <> 0
ORDER BY h.FLASTNAME, h.FFIRSTNAME, h.FMIDDLENAME, r.FCARDNUM;

PRINT '=== 8. DO NOT use TAPCSYSEVENTSCOMMON for employee access ===';
PRINT 'It is the APACS internal/system event stream. It is intentionally not queried here as an employee access journal.';
GO
