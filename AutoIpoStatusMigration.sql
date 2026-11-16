-- Automatic IPO Status Management Database Migration
-- Ensures Opening_time and Closing_time columns exist on [dbo].[IPOs]

IF COL_LENGTH(N'[dbo].[IPOs]', N'Opening_time') IS NULL
BEGIN
    ALTER TABLE [dbo].[IPOs] ADD [Opening_time] time NULL;
END
GO

IF COL_LENGTH(N'[dbo].[IPOs]', N'Closing_time') IS NULL
BEGIN
    ALTER TABLE [dbo].[IPOs] ADD [Closing_time] time NULL;
END
GO

-- Set default opening and closing times for existing IPOs where null
UPDATE [dbo].[IPOs]
SET [Opening_time] = '09:30:00'
WHERE [Opening_time] IS NULL;
GO

UPDATE [dbo].[IPOs]
SET [Closing_time] = '15:30:00'
WHERE [Closing_time] IS NULL;
GO

-- Ensure Vertex Renewable Energy IPO has exact configured opening and closing times
UPDATE [dbo].[IPOs]
SET [Opening_time] = '09:30:00',
    [Closing_time] = '15:30:00',
    [Open_date] = '2026-12-07',
    [Close_date] = '2026-12-09',
    [Status] = 'Upcoming'
WHERE [IPO_name] LIKE '%Vertex Renewable Energy%';
GO
