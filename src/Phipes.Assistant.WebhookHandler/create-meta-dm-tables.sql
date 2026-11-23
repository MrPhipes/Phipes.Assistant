-- =============================================================================
-- PacificDev.Social — mensajes directos de Instagram/Facebook.
-- Cola que usa MetaProactiveNotifier (aviso, recordatorio, vencimiento) y la skill
-- /meta-mensaje de Sarah (envía la respuesta aprobada por Felipe y cierra la fila).
--
-- NO se ejecuta automáticamente desde el código: se corre una vez a mano (sqlcmd /
-- SSMS) sobre la base PhipesAssistant, en el mismo despliegue que el handler.
-- Idempotente: se puede correr de nuevo sin efectos.
-- =============================================================================

USE [PhipesAssistant];
GO

IF OBJECT_ID(N'dbo.MetaDmPending', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MetaDmPending
    (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MetaDmPending PRIMARY KEY,
        EventId         BIGINT            NOT NULL,              -- PacificDevMeta.dbo.WebhookEvents.Id
        Platform        NVARCHAR(16)      NOT NULL,              -- 'instagram' | 'facebook'
        MessageId       NVARCHAR(256)     NOT NULL,              -- mid de Meta
        SenderId        NVARCHAR(128)     NOT NULL,              -- IGSID / PSID: destinatario de la respuesta
        Author          NVARCHAR(256)     NOT NULL,              -- '@usuario' o genérico
        MessageText     NVARCHAR(MAX)     NOT NULL,
        ProposedReply   NVARCHAR(MAX)     NULL,                  -- borrador de MetaReplyDrafter
        SentUtc         DATETIME2         NOT NULL,              -- cuándo escribió la persona
        DeadlineUtc     DATETIME2         NOT NULL,              -- SentUtc + ventana de Meta
        NotifiedUtc     DATETIME2         NOT NULL CONSTRAINT DF_MetaDmPending_NotifiedUtc DEFAULT SYSUTCDATETIME(),
        ReminderSentUtc DATETIME2         NULL,
        Status          NVARCHAR(16)      NOT NULL CONSTRAINT DF_MetaDmPending_Status DEFAULT N'awaiting',
                                                                 -- awaiting | done | ignored | expired | superseded
        ResolvedUtc     DATETIME2         NULL,
        Resolution      NVARCHAR(MAX)     NULL,                  -- texto enviado o motivo
        UsedHumanAgent  BIT               NOT NULL CONSTRAINT DF_MetaDmPending_UsedHumanAgent DEFAULT 0
    );

    -- Idempotencia: Meta reintenta el mismo webhook; un mensaje se avisa una sola vez.
    CREATE UNIQUE INDEX UX_MetaDmPending_Message ON dbo.MetaDmPending (Platform, MessageId);

    -- Query del notifier (recordatorios/vencimientos) y de la skill (último pendiente).
    CREATE INDEX IX_MetaDmPending_Status_Deadline ON dbo.MetaDmPending (Status, DeadlineUtc);
    CREATE INDEX IX_MetaDmPending_Sender ON dbo.MetaDmPending (Platform, SenderId, Status);
END
GO
