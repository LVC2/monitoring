USE [apacs3000rus];
GO

/*
    APACS photo diagnostics. READ ONLY.
    Goal: find every binary/image-like column that actually contains data.
*/
SET NOCOUNT ON;

PRINT '=== Binary columns in database ===';
SELECT
    c.TABLE_SCHEMA,
    c.TABLE_NAME,
    c.ORDINAL_POSITION,
    c.COLUMN_NAME,
    c.DATA_TYPE,
    c.CHARACTER_MAXIMUM_LENGTH
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.DATA_TYPE IN ('image', 'varbinary', 'binary')
ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION;

PRINT '=== Candidate photo columns with non-empty rows ===';
DECLARE @sql nvarchar(max) = N'';

SELECT @sql = @sql +
    N'SELECT N''' + REPLACE(c.TABLE_SCHEMA + N'.' + c.TABLE_NAME, '''', '''''') +
    N''' AS TableName, N''' + REPLACE(c.COLUMN_NAME, '''', '''''') +
    N''' AS ColumnName, COUNT_BIG(*) AS NonEmptyRows, MAX(DATALENGTH(' +
    QUOTENAME(c.COLUMN_NAME) + N')) AS MaxBytes ' +
    N'FROM ' + QUOTENAME(c.TABLE_SCHEMA) + N'.' + QUOTENAME(c.TABLE_NAME) +
    N' WHERE ' + QUOTENAME(c.COLUMN_NAME) + N' IS NOT NULL' +
    N' AND DATALENGTH(' + QUOTENAME(c.COLUMN_NAME) + N') > 0' +
    N' HAVING COUNT_BIG(*) > 0 UNION ALL '
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.DATA_TYPE IN ('image', 'varbinary', 'binary');

IF LEN(@sql) > 0
BEGIN
    SET @sql = LEFT(@sql, LEN(@sql) - LEN(N' UNION ALL '));
    SET @sql += N' ORDER BY MaxBytes DESC, TableName, ColumnName;';
    EXEC sys.sp_executesql @sql;
END;
GO
