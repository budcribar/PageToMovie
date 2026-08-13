using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Core.Models;

#region Monetization & Business Logic Enums (201-220)

/// <summary>
/// Subscription tier levels available for user accounts.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubscriptionTierKind
{
    Free,
    Starter,
    Professional,
    Team,
    Enterprise,
    Custom
}

/// <summary>
/// Billing cycle frequencies for paid plans.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BillingCycleKind
{
    Monthly,
    Annual,
    Quarterly,
    SemiAnnual,
    PayAsYouGo,
    OneTime
}

/// <summary>
/// Pre-packaged credit bundles for AI generation rendering.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CreditPackageKind
{
    StarterPack,
    CreatorPack,
    StudioPack,
    EnterprisePack,
    CustomTopUp,
    PromotionalGrant
}

/// <summary>
/// Third-party payment processing gateways.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentGatewayKind
{
    Stripe,
    PayPal,
    Adyen,
    Braintree,
    ApplePay,
    GooglePay,
    WireTransfer,
    ManualInvoice
}

/// <summary>
/// Licensing terms applied to exported media artifacts.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LicenseTypeKind
{
    Personal,
    Commercial,
    RoyaltyFree,
    Exclusive,
    Educational,
    NonProfit,
    EnterpriseSite
}

/// <summary>
/// Metered usage quota resource dimensions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UsageQuotaKind
{
    RenderSeconds,
    StorageGigabytes,
    ApiRequests,
    AiModelTokens,
    ExportResolution,
    ConcurrentJobs
}

/// <summary>
/// Referral partner tier classification levels.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AffiliateTierKind
{
    Bronze,
    Silver,
    Gold,
    Platinum,
    Diamond,
    Ambassador
}

/// <summary>
/// Status states of generated customer billing invoices.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InvoiceStatusKind
{
    Draft,
    Open,
    Paid,
    Uncollectible,
    Void,
    PastDue,
    Processing
}

/// <summary>
/// Tax exemption status classifications for invoicing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaxExemptionStatus
{
    None,
    Exempt,
    ReverseCharge,
    PendingVerification,
    DirectPay,
    Government
}

/// <summary>
/// Types of promotional discount structures.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiscountTypeKind
{
    Percentage,
    FixedAmount,
    CreditGrant,
    FreeTrialDays,
    TierUpgrade
}

/// <summary>
/// Duration limits applied to promotional coupons.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CouponDurationKind
{
    Once,
    Repeating,
    Forever,
    CustomPeriod
}

/// <summary>
/// Supported payment method instruments.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentMethodKind
{
    CreditCard,
    DebitCard,
    BankTransfer,
    DigitalWallet,
    Crypto,
    DirectDebit,
    PurchaseOrder
}

/// <summary>
/// ISO standard currency codes supported for transaction settlement.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CurrencyCodeKind
{
    Usd,
    Eur,
    Gbp,
    Cad,
    Aud,
    Jpy,
    Cny,
    Inr,
    Brl,
    Chf
}

/// <summary>
/// Categorized reasons for issuing customer payment refunds.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RefundReasonKind
{
    DuplicatePayment,
    ServiceOutage,
    CustomerDissatisfaction,
    AccidentalPurchase,
    Fraudulent,
    SystemError
}

/// <summary>
/// Resource boundary limit dimensions per account tier.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccountTierLimitKind
{
    MaxProjects,
    MaxStorageGb,
    MaxExportResolution,
    MaxConcurrentRenders,
    MaxTeamMembers,
    MaxMonthlyCredits
}

/// <summary>
/// External partner software integration channels.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PartnerIntegrationType
{
    Zapier,
    Make,
    Slack,
    Discord,
    AdobeCreativeCloud,
    Figma,
    CustomWebhook
}

/// <summary>
/// User notification delivery channels.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationPreferenceKind
{
    Email,
    InApp,
    Push,
    Sms,
    Webhook,
    None
}

/// <summary>
/// Compliance and policy consent types collected from users.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserConsentType
{
    TermsOfService,
    PrivacyPolicy,
    MarketingEmail,
    AnalyticsTracking,
    CookiePolicy,
    DataSharing
}

/// <summary>
/// Customer support ticket priority levels.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupportTicketPriority
{
    Low,
    Medium,
    High,
    Urgent,
    Critical
}

/// <summary>
/// Operational category classifications for support tickets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupportTicketCategory
{
    Billing,
    Technical,
    AccountAccess,
    FeatureRequest,
    BugReport,
    Security
}

#endregion

#region Extension Methods

/// <summary>
/// Extension methods for Business and Monetization enums (201-220).
/// </summary>
public static class BusinessAndMonetizationEnumExtensions
{
    private const string CustomApi = "custom";

    public static string ToApiString(this SubscriptionTierKind val) => val switch
    {
        SubscriptionTierKind.Free => "free",
        SubscriptionTierKind.Starter => "starter",
        SubscriptionTierKind.Professional => "professional",
        SubscriptionTierKind.Team => "team",
        SubscriptionTierKind.Enterprise => "enterprise",
        SubscriptionTierKind.Custom => CustomApi,
        _ => "free"
    };

    public static string ToApiString(this BillingCycleKind val) => val switch
    {
        BillingCycleKind.Monthly => "monthly",
        BillingCycleKind.Annual => "annual",
        BillingCycleKind.Quarterly => "quarterly",
        BillingCycleKind.SemiAnnual => "semi_annual",
        BillingCycleKind.PayAsYouGo => "pay_as_you_go",
        BillingCycleKind.OneTime => "one_time",
        _ => "monthly"
    };

    public static string ToApiString(this CreditPackageKind val) => val switch
    {
        CreditPackageKind.StarterPack => "starter_pack",
        CreditPackageKind.CreatorPack => "creator_pack",
        CreditPackageKind.StudioPack => "studio_pack",
        CreditPackageKind.EnterprisePack => "enterprise_pack",
        CreditPackageKind.CustomTopUp => "custom_top_up",
        CreditPackageKind.PromotionalGrant => "promotional_grant",
        _ => "starter_pack"
    };

    public static string ToApiString(this PaymentGatewayKind val) => val switch
    {
        PaymentGatewayKind.Stripe => "stripe",
        PaymentGatewayKind.PayPal => "paypal",
        PaymentGatewayKind.Adyen => "adyen",
        PaymentGatewayKind.Braintree => "braintree",
        PaymentGatewayKind.ApplePay => "apple_pay",
        PaymentGatewayKind.GooglePay => "google_pay",
        PaymentGatewayKind.WireTransfer => "wire_transfer",
        PaymentGatewayKind.ManualInvoice => "manual_invoice",
        _ => "stripe"
    };

    public static string ToApiString(this LicenseTypeKind val) => val switch
    {
        LicenseTypeKind.Personal => "personal",
        LicenseTypeKind.Commercial => "commercial",
        LicenseTypeKind.RoyaltyFree => "royalty_free",
        LicenseTypeKind.Exclusive => "exclusive",
        LicenseTypeKind.Educational => "educational",
        LicenseTypeKind.NonProfit => "non_profit",
        LicenseTypeKind.EnterpriseSite => "enterprise_site",
        _ => "personal"
    };

    public static string ToApiString(this UsageQuotaKind val) => val switch
    {
        UsageQuotaKind.RenderSeconds => "render_seconds",
        UsageQuotaKind.StorageGigabytes => "storage_gigabytes",
        UsageQuotaKind.ApiRequests => "api_requests",
        UsageQuotaKind.AiModelTokens => "ai_model_tokens",
        UsageQuotaKind.ExportResolution => "export_resolution",
        UsageQuotaKind.ConcurrentJobs => "concurrent_jobs",
        _ => "render_seconds"
    };

    public static string ToApiString(this AffiliateTierKind val) => val switch
    {
        AffiliateTierKind.Bronze => "bronze",
        AffiliateTierKind.Silver => "silver",
        AffiliateTierKind.Gold => "gold",
        AffiliateTierKind.Platinum => "platinum",
        AffiliateTierKind.Diamond => "diamond",
        AffiliateTierKind.Ambassador => "ambassador",
        _ => "bronze"
    };

    public static string ToApiString(this InvoiceStatusKind val) => val switch
    {
        InvoiceStatusKind.Draft => "draft",
        InvoiceStatusKind.Open => "open",
        InvoiceStatusKind.Paid => "paid",
        InvoiceStatusKind.Uncollectible => "uncollectible",
        InvoiceStatusKind.Void => "void",
        InvoiceStatusKind.PastDue => "past_due",
        InvoiceStatusKind.Processing => "processing",
        _ => "open"
    };

    public static string ToApiString(this TaxExemptionStatus val) => val switch
    {
        TaxExemptionStatus.None => "none",
        TaxExemptionStatus.Exempt => "exempt",
        TaxExemptionStatus.ReverseCharge => "reverse_charge",
        TaxExemptionStatus.PendingVerification => "pending_verification",
        TaxExemptionStatus.DirectPay => "direct_pay",
        TaxExemptionStatus.Government => "government",
        _ => "none"
    };

    public static string ToApiString(this DiscountTypeKind val) => val switch
    {
        DiscountTypeKind.Percentage => "percentage",
        DiscountTypeKind.FixedAmount => "fixed_amount",
        DiscountTypeKind.CreditGrant => "credit_grant",
        DiscountTypeKind.FreeTrialDays => "free_trial_days",
        DiscountTypeKind.TierUpgrade => "tier_upgrade",
        _ => "percentage"
    };

    public static string ToApiString(this CouponDurationKind val) => val switch
    {
        CouponDurationKind.Once => "once",
        CouponDurationKind.Repeating => "repeating",
        CouponDurationKind.Forever => "forever",
        CouponDurationKind.CustomPeriod => "custom_period",
        _ => "once"
    };

    public static string ToApiString(this PaymentMethodKind val) => val switch
    {
        PaymentMethodKind.CreditCard => "credit_card",
        PaymentMethodKind.DebitCard => "debit_card",
        PaymentMethodKind.BankTransfer => "bank_transfer",
        PaymentMethodKind.DigitalWallet => "digital_wallet",
        PaymentMethodKind.Crypto => "crypto",
        PaymentMethodKind.DirectDebit => "direct_debit",
        PaymentMethodKind.PurchaseOrder => "purchase_order",
        _ => "credit_card"
    };

    public static string ToApiString(this CurrencyCodeKind val) => val switch
    {
        CurrencyCodeKind.Usd => "usd",
        CurrencyCodeKind.Eur => "eur",
        CurrencyCodeKind.Gbp => "gbp",
        CurrencyCodeKind.Cad => "cad",
        CurrencyCodeKind.Aud => "aud",
        CurrencyCodeKind.Jpy => "jpy",
        CurrencyCodeKind.Cny => "cny",
        CurrencyCodeKind.Inr => "inr",
        CurrencyCodeKind.Brl => "brl",
        CurrencyCodeKind.Chf => "chf",
        _ => "usd"
    };

    public static string ToApiString(this RefundReasonKind val) => val switch
    {
        RefundReasonKind.DuplicatePayment => "duplicate_payment",
        RefundReasonKind.ServiceOutage => "service_outage",
        RefundReasonKind.CustomerDissatisfaction => "customer_dissatisfaction",
        RefundReasonKind.AccidentalPurchase => "accidental_purchase",
        RefundReasonKind.Fraudulent => "fraudulent",
        RefundReasonKind.SystemError => "system_error",
        _ => "customer_dissatisfaction"
    };

    public static string ToApiString(this AccountTierLimitKind val) => val switch
    {
        AccountTierLimitKind.MaxProjects => "max_projects",
        AccountTierLimitKind.MaxStorageGb => "max_storage_gb",
        AccountTierLimitKind.MaxExportResolution => "max_export_resolution",
        AccountTierLimitKind.MaxConcurrentRenders => "max_concurrent_renders",
        AccountTierLimitKind.MaxTeamMembers => "max_team_members",
        AccountTierLimitKind.MaxMonthlyCredits => "max_monthly_credits",
        _ => "max_projects"
    };

    public static string ToApiString(this PartnerIntegrationType val) => val switch
    {
        PartnerIntegrationType.Zapier => "zapier",
        PartnerIntegrationType.Make => "make",
        PartnerIntegrationType.Slack => "slack",
        PartnerIntegrationType.Discord => "discord",
        PartnerIntegrationType.AdobeCreativeCloud => "adobe_creative_cloud",
        PartnerIntegrationType.Figma => "figma",
        PartnerIntegrationType.CustomWebhook => "custom_webhook",
        _ => "zapier"
    };

    public static string ToApiString(this NotificationPreferenceKind val) => val switch
    {
        NotificationPreferenceKind.Email => "email",
        NotificationPreferenceKind.InApp => "in_app",
        NotificationPreferenceKind.Push => "push",
        NotificationPreferenceKind.Sms => "sms",
        NotificationPreferenceKind.Webhook => "webhook",
        NotificationPreferenceKind.None => "none",
        _ => "email"
    };

    public static string ToApiString(this UserConsentType val) => val switch
    {
        UserConsentType.TermsOfService => "terms_of_service",
        UserConsentType.PrivacyPolicy => "privacy_policy",
        UserConsentType.MarketingEmail => "marketing_email",
        UserConsentType.AnalyticsTracking => "analytics_tracking",
        UserConsentType.CookiePolicy => "cookie_policy",
        UserConsentType.DataSharing => "data_sharing",
        _ => "terms_of_service"
    };

    public static string ToApiString(this SupportTicketPriority val) => val switch
    {
        SupportTicketPriority.Low => "low",
        SupportTicketPriority.Medium => "medium",
        SupportTicketPriority.High => "high",
        SupportTicketPriority.Urgent => "urgent",
        SupportTicketPriority.Critical => "critical",
        _ => "medium"
    };

    public static string ToApiString(this SupportTicketCategory val) => val switch
    {
        SupportTicketCategory.Billing => "billing",
        SupportTicketCategory.Technical => "technical",
        SupportTicketCategory.AccountAccess => "account_access",
        SupportTicketCategory.FeatureRequest => "feature_request",
        SupportTicketCategory.BugReport => "bug_report",
        SupportTicketCategory.Security => "security",
        _ => "technical"
    };

    public static SubscriptionTierKind ParseSubscriptionTierKind(string? s, SubscriptionTierKind defaultValue = SubscriptionTierKind.Free)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "free" => SubscriptionTierKind.Free,
            "starter" => SubscriptionTierKind.Starter,
            "professional" or "pro" => SubscriptionTierKind.Professional,
            "team" => SubscriptionTierKind.Team,
            "enterprise" => SubscriptionTierKind.Enterprise,
            CustomApi => SubscriptionTierKind.Custom,
            _ => Enum.TryParse<SubscriptionTierKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static BillingCycleKind ParseBillingCycleKind(string? s, BillingCycleKind defaultValue = BillingCycleKind.Monthly)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "monthly" => BillingCycleKind.Monthly,
            "annual" or "yearly" => BillingCycleKind.Annual,
            "quarterly" => BillingCycleKind.Quarterly,
            "semi_annual" or "semiannual" => BillingCycleKind.SemiAnnual,
            "pay_as_you_go" or "payg" => BillingCycleKind.PayAsYouGo,
            "one_time" or "onetime" => BillingCycleKind.OneTime,
            _ => Enum.TryParse<BillingCycleKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static CreditPackageKind ParseCreditPackageKind(string? s, CreditPackageKind defaultValue = CreditPackageKind.StarterPack)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "starter_pack" or "starter" => CreditPackageKind.StarterPack,
            "creator_pack" or "creator" => CreditPackageKind.CreatorPack,
            "studio_pack" or "studio" => CreditPackageKind.StudioPack,
            "enterprise_pack" or "enterprise" => CreditPackageKind.EnterprisePack,
            "custom_top_up" or CustomApi => CreditPackageKind.CustomTopUp,
            "promotional_grant" or "promo" => CreditPackageKind.PromotionalGrant,
            _ => Enum.TryParse<CreditPackageKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static PaymentGatewayKind ParsePaymentGatewayKind(string? s, PaymentGatewayKind defaultValue = PaymentGatewayKind.Stripe)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "stripe" => PaymentGatewayKind.Stripe,
            "paypal" => PaymentGatewayKind.PayPal,
            "adyen" => PaymentGatewayKind.Adyen,
            "braintree" => PaymentGatewayKind.Braintree,
            "apple_pay" or "applepay" => PaymentGatewayKind.ApplePay,
            "google_pay" or "googlepay" => PaymentGatewayKind.GooglePay,
            "wire_transfer" or "wire" => PaymentGatewayKind.WireTransfer,
            "manual_invoice" or "invoice" => PaymentGatewayKind.ManualInvoice,
            _ => Enum.TryParse<PaymentGatewayKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static LicenseTypeKind ParseLicenseTypeKind(string? s, LicenseTypeKind defaultValue = LicenseTypeKind.Personal)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "personal" => LicenseTypeKind.Personal,
            "commercial" => LicenseTypeKind.Commercial,
            "royalty_free" or "royaltyfree" => LicenseTypeKind.RoyaltyFree,
            "exclusive" => LicenseTypeKind.Exclusive,
            "educational" or "edu" => LicenseTypeKind.Educational,
            "non_profit" or "nonprofit" => LicenseTypeKind.NonProfit,
            "enterprise_site" or "site" => LicenseTypeKind.EnterpriseSite,
            _ => Enum.TryParse<LicenseTypeKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static UsageQuotaKind ParseUsageQuotaKind(string? s, UsageQuotaKind defaultValue = UsageQuotaKind.RenderSeconds)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "render_seconds" or "seconds" => UsageQuotaKind.RenderSeconds,
            "storage_gigabytes" or "storage_gb" or "storage" => UsageQuotaKind.StorageGigabytes,
            "api_requests" or "requests" => UsageQuotaKind.ApiRequests,
            "ai_model_tokens" or "tokens" => UsageQuotaKind.AiModelTokens,
            "export_resolution" or "resolution" => UsageQuotaKind.ExportResolution,
            "concurrent_jobs" or "concurrency" => UsageQuotaKind.ConcurrentJobs,
            _ => Enum.TryParse<UsageQuotaKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static AffiliateTierKind ParseAffiliateTierKind(string? s, AffiliateTierKind defaultValue = AffiliateTierKind.Bronze)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "bronze" => AffiliateTierKind.Bronze,
            "silver" => AffiliateTierKind.Silver,
            "gold" => AffiliateTierKind.Gold,
            "platinum" => AffiliateTierKind.Platinum,
            "diamond" => AffiliateTierKind.Diamond,
            "ambassador" => AffiliateTierKind.Ambassador,
            _ => Enum.TryParse<AffiliateTierKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static InvoiceStatusKind ParseInvoiceStatusKind(string? s, InvoiceStatusKind defaultValue = InvoiceStatusKind.Open)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "draft" => InvoiceStatusKind.Draft,
            "open" => InvoiceStatusKind.Open,
            "paid" => InvoiceStatusKind.Paid,
            "uncollectible" => InvoiceStatusKind.Uncollectible,
            "void" => InvoiceStatusKind.Void,
            "past_due" or "pastdue" => InvoiceStatusKind.PastDue,
            "processing" => InvoiceStatusKind.Processing,
            _ => Enum.TryParse<InvoiceStatusKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static TaxExemptionStatus ParseTaxExemptionStatus(string? s, TaxExemptionStatus defaultValue = TaxExemptionStatus.None)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "none" => TaxExemptionStatus.None,
            "exempt" => TaxExemptionStatus.Exempt,
            "reverse_charge" or "reversecharge" => TaxExemptionStatus.ReverseCharge,
            "pending_verification" or "pending" => TaxExemptionStatus.PendingVerification,
            "direct_pay" or "directpay" => TaxExemptionStatus.DirectPay,
            "government" or "gov" => TaxExemptionStatus.Government,
            _ => Enum.TryParse<TaxExemptionStatus>(s, true, out var r) ? r : defaultValue
        };
    }

    public static DiscountTypeKind ParseDiscountTypeKind(string? s, DiscountTypeKind defaultValue = DiscountTypeKind.Percentage)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "percentage" or "percent" => DiscountTypeKind.Percentage,
            "fixed_amount" or "fixed" => DiscountTypeKind.FixedAmount,
            "credit_grant" or "credits" => DiscountTypeKind.CreditGrant,
            "free_trial_days" or "trial" => DiscountTypeKind.FreeTrialDays,
            "tier_upgrade" or "upgrade" => DiscountTypeKind.TierUpgrade,
            _ => Enum.TryParse<DiscountTypeKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static CouponDurationKind ParseCouponDurationKind(string? s, CouponDurationKind defaultValue = CouponDurationKind.Once)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "once" => CouponDurationKind.Once,
            "repeating" => CouponDurationKind.Repeating,
            "forever" => CouponDurationKind.Forever,
            "custom_period" or CustomApi => CouponDurationKind.CustomPeriod,
            _ => Enum.TryParse<CouponDurationKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static PaymentMethodKind ParsePaymentMethodKind(string? s, PaymentMethodKind defaultValue = PaymentMethodKind.CreditCard)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "credit_card" or "creditcard" or "card" => PaymentMethodKind.CreditCard,
            "debit_card" or "debitcard" => PaymentMethodKind.DebitCard,
            "bank_transfer" or "ach" or "wire" => PaymentMethodKind.BankTransfer,
            "digital_wallet" or "wallet" => PaymentMethodKind.DigitalWallet,
            "crypto" => PaymentMethodKind.Crypto,
            "direct_debit" => PaymentMethodKind.DirectDebit,
            "purchase_order" or "po" => PaymentMethodKind.PurchaseOrder,
            _ => Enum.TryParse<PaymentMethodKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static CurrencyCodeKind ParseCurrencyCodeKind(string? s, CurrencyCodeKind defaultValue = CurrencyCodeKind.Usd)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant();
        return clean switch
        {
            "usd" => CurrencyCodeKind.Usd,
            "eur" => CurrencyCodeKind.Eur,
            "gbp" => CurrencyCodeKind.Gbp,
            "cad" => CurrencyCodeKind.Cad,
            "aud" => CurrencyCodeKind.Aud,
            "jpy" => CurrencyCodeKind.Jpy,
            "cny" => CurrencyCodeKind.Cny,
            "inr" => CurrencyCodeKind.Inr,
            "brl" => CurrencyCodeKind.Brl,
            "chf" => CurrencyCodeKind.Chf,
            _ => Enum.TryParse<CurrencyCodeKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static RefundReasonKind ParseRefundReasonKind(string? s, RefundReasonKind defaultValue = RefundReasonKind.CustomerDissatisfaction)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "duplicate_payment" or "duplicate" => RefundReasonKind.DuplicatePayment,
            "service_outage" or "outage" => RefundReasonKind.ServiceOutage,
            "customer_dissatisfaction" or "dissatisfied" => RefundReasonKind.CustomerDissatisfaction,
            "accidental_purchase" or "accidental" => RefundReasonKind.AccidentalPurchase,
            "fraudulent" or "fraud" => RefundReasonKind.Fraudulent,
            "system_error" or "error" => RefundReasonKind.SystemError,
            _ => Enum.TryParse<RefundReasonKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static AccountTierLimitKind ParseAccountTierLimitKind(string? s, AccountTierLimitKind defaultValue = AccountTierLimitKind.MaxProjects)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "max_projects" or "projects" => AccountTierLimitKind.MaxProjects,
            "max_storage_gb" or "storage" => AccountTierLimitKind.MaxStorageGb,
            "max_export_resolution" or "resolution" => AccountTierLimitKind.MaxExportResolution,
            "max_concurrent_renders" or "concurrency" => AccountTierLimitKind.MaxConcurrentRenders,
            "max_team_members" or "team_members" => AccountTierLimitKind.MaxTeamMembers,
            "max_monthly_credits" or "monthly_credits" => AccountTierLimitKind.MaxMonthlyCredits,
            _ => Enum.TryParse<AccountTierLimitKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static PartnerIntegrationType ParsePartnerIntegrationType(string? s, PartnerIntegrationType defaultValue = PartnerIntegrationType.Zapier)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "zapier" => PartnerIntegrationType.Zapier,
            "make" or "integromat" => PartnerIntegrationType.Make,
            "slack" => PartnerIntegrationType.Slack,
            "discord" => PartnerIntegrationType.Discord,
            "adobe_creative_cloud" or "adobe" => PartnerIntegrationType.AdobeCreativeCloud,
            "figma" => PartnerIntegrationType.Figma,
            "custom_webhook" or "webhook" => PartnerIntegrationType.CustomWebhook,
            _ => Enum.TryParse<PartnerIntegrationType>(s, true, out var r) ? r : defaultValue
        };
    }

    public static NotificationPreferenceKind ParseNotificationPreferenceKind(string? s, NotificationPreferenceKind defaultValue = NotificationPreferenceKind.Email)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "email" => NotificationPreferenceKind.Email,
            "in_app" or "inapp" => NotificationPreferenceKind.InApp,
            "push" => NotificationPreferenceKind.Push,
            "sms" => NotificationPreferenceKind.Sms,
            "webhook" => NotificationPreferenceKind.Webhook,
            "none" => NotificationPreferenceKind.None,
            _ => Enum.TryParse<NotificationPreferenceKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static UserConsentType ParseUserConsentType(string? s, UserConsentType defaultValue = UserConsentType.TermsOfService)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "terms_of_service" or "tos" or "terms" => UserConsentType.TermsOfService,
            "privacy_policy" or "privacy" => UserConsentType.PrivacyPolicy,
            "marketing_email" or "marketing" => UserConsentType.MarketingEmail,
            "analytics_tracking" or "analytics" => UserConsentType.AnalyticsTracking,
            "cookie_policy" or "cookies" => UserConsentType.CookiePolicy,
            "data_sharing" or "datasharing" => UserConsentType.DataSharing,
            _ => Enum.TryParse<UserConsentType>(s, true, out var r) ? r : defaultValue
        };
    }

    public static SupportTicketPriority ParseSupportTicketPriority(string? s, SupportTicketPriority defaultValue = SupportTicketPriority.Medium)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant();
        return clean switch
        {
            "low" => SupportTicketPriority.Low,
            "medium" or "normal" => SupportTicketPriority.Medium,
            "high" => SupportTicketPriority.High,
            "urgent" => SupportTicketPriority.Urgent,
            "critical" => SupportTicketPriority.Critical,
            _ => Enum.TryParse<SupportTicketPriority>(s, true, out var r) ? r : defaultValue
        };
    }

    public static SupportTicketCategory ParseSupportTicketCategory(string? s, SupportTicketCategory defaultValue = SupportTicketCategory.Technical)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "billing" => SupportTicketCategory.Billing,
            "technical" or "tech" => SupportTicketCategory.Technical,
            "account_access" or "account" => SupportTicketCategory.AccountAccess,
            "feature_request" or "feature" => SupportTicketCategory.FeatureRequest,
            "bug_report" or "bug" => SupportTicketCategory.BugReport,
            "security" => SupportTicketCategory.Security,
            _ => Enum.TryParse<SupportTicketCategory>(s, true, out var r) ? r : defaultValue
        };
    }

}

#endregion
