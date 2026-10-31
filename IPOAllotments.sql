IF OBJECT_ID(N'[IPOAllotments]', N'U') IS NULL
BEGIN
    CREATE TABLE [IPOAllotments] (
        [Allotment_id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_IPOAllotments] PRIMARY KEY,
        [Application_id] int NOT NULL,
        [User_id] int NOT NULL,
        [IPO_id] int NOT NULL,
        [Allotted_lots] int NOT NULL,
        [Allotted_shares] int NOT NULL,
        [Allotment_price] decimal(18,2) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [Allotment_date] datetime2 NOT NULL,
        CONSTRAINT [FK_IPOAllotments_IPOApplications] FOREIGN KEY ([Application_id]) REFERENCES [IPOApplications] ([Application_id]),
        CONSTRAINT [FK_IPOAllotments_Users] FOREIGN KEY ([User_id]) REFERENCES [Users] ([User_id]),
        CONSTRAINT [FK_IPOAllotments_IPOs] FOREIGN KEY ([IPO_id]) REFERENCES [IPOs] ([IPO_id]),
        CONSTRAINT [UQ_IPOAllotments_Application] UNIQUE ([Application_id])
    );
    CREATE INDEX [IX_IPOAllotments_User_id] ON [IPOAllotments] ([User_id]);
    CREATE INDEX [IX_IPOAllotments_IPO_id] ON [IPOAllotments] ([IPO_id]);
END
