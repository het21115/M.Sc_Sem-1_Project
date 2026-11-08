IF COL_LENGTH(N'[Users]', N'SimulatedCashBalance') IS NULL
    ALTER TABLE [Users] ADD [SimulatedCashBalance] decimal(18,2) NOT NULL
        CONSTRAINT [DF_Users_SimulatedCashBalance] DEFAULT (100000);
IF COL_LENGTH(N'[IPOListing]', N'StockSymbol') IS NULL
    ALTER TABLE [IPOListing] ADD [StockSymbol] nvarchar(30) NULL;
IF COL_LENGTH(N'[IPOListing]', N'Exchange') IS NULL
    ALTER TABLE [IPOListing] ADD [Exchange] nvarchar(10) NULL;
IF OBJECT_ID(N'[MarketTradingHolidays]', N'U') IS NULL
BEGIN
    CREATE TABLE [MarketTradingHolidays] (
        [HolidayId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_MarketTradingHolidays] PRIMARY KEY,
        [HolidayDate] date NOT NULL,
        [Name] nvarchar(150) NOT NULL,
        CONSTRAINT [UQ_MarketTradingHolidays_HolidayDate] UNIQUE ([HolidayDate])
    );
END;
IF OBJECT_ID(N'[StockMarketData]', N'U') IS NULL
BEGIN
    CREATE TABLE [StockMarketData] (
        [MarketDataId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_StockMarketData] PRIMARY KEY,
        [IPOId] int NOT NULL,
        [CompanyId] int NOT NULL,
        [StockSymbol] nvarchar(30) NOT NULL,
        [Exchange] nvarchar(10) NOT NULL,
        [IssuePrice] decimal(18,2) NULL,
        [ListingPrice] decimal(18,2) NULL,
        [CurrentPrice] decimal(18,2) NULL,
        [PreviousClose] decimal(18,2) NULL,
        [DayHigh] decimal(18,2) NULL,
        [DayLow] decimal(18,2) NULL,
        [Volume] bigint NULL,
        [PriceChange] decimal(18,2) NULL,
        [PriceChangePercentage] decimal(10,4) NULL,
        [LastUpdated] datetime2 NOT NULL,
        [IsAvailable] bit NOT NULL CONSTRAINT [DF_StockMarketData_IsAvailable] DEFAULT (0),
        CONSTRAINT [FK_StockMarketData_IPO] FOREIGN KEY ([IPOId]) REFERENCES [IPOs] ([IPO_id]) ON DELETE CASCADE,
        CONSTRAINT [FK_StockMarketData_Company] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Company_id])
    );
    CREATE UNIQUE INDEX [IX_StockMarketData_IPOId_Exchange] ON [StockMarketData] ([IPOId], [Exchange]);
    CREATE INDEX [IX_StockMarketData_StockSymbol] ON [StockMarketData] ([StockSymbol]);
END;
