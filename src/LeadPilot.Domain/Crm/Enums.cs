namespace LeadPilot.Domain.Crm;

/// <summary>Channel a consent applies to and a message would be sent over.</summary>
public enum ConsentChannel { WhatsApp = 1, Sms = 2, Email = 3, Phone = 4 }

/// <summary>Scope of a suppression entry. <see cref="Any"/> blocks the value on every channel.</summary>
public enum SuppressionChannel { Any = 0, WhatsApp = 1, Sms = 2, Email = 3, Phone = 4 }

public enum ConsentPurpose { Marketing = 1, Service = 2 }
public enum ConsentStatus { Granted = 1, Revoked = 2 }
public enum SuppressionReason { OptOut = 1, Invalid = 2, Blocked = 3, TenantExcluded = 4, Legal = 5 }

/// <summary>How trustworthy a contact record is: scraped/public, supplied by the lead, or verified by the team.</summary>
public enum DataOrigin { Public = 1, Provided = 2, Verified = 3 }

public enum StageKind { Open = 1, Won = 2, Lost = 3, Suppressed = 4 }

public enum LeadSourceType { Manual = 1, Import = 2, Api = 3, Discovery = 4, Referral = 5, Website = 6, Other = 7 }

public enum LeadActivityType
{
    Created = 1, Updated, StageChanged, Assigned, Unassigned, NoteAdded, NoteDeleted, TagAdded, TagRemoved,
    ContactAdded, ContactUpdated, ContactRemoved, ConsentRecorded, Imported, Merged, Archived, Restored, OpportunityCreated, OpportunityUpdated
}

public enum ImportFileType { Csv = 1, Xlsx = 2 }
public enum ImportStatus { Uploaded = 1, Processing = 2, Completed = 3, Failed = 4 }
public enum DuplicatePolicy { Skip = 1, Merge = 2, Review = 3 }
public enum ImportErrorKind { Validation = 1, Duplicate = 2 }
public enum DuplicateResolution { Pending = 1, Skipped = 2, Merged = 3 }

public enum ScoreDimension { BusinessCategory = 1, Location, WebsiteAvailability, ContactQuality, Activity, VerifiedInfo, Engagement, Interest }

public enum OpportunityStatus { Open = 1, Won = 2, Lost = 3 }
