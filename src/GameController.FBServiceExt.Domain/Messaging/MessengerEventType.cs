namespace GameController.FBServiceExt.Domain.Messaging;

public enum MessengerEventType
{
    Unknown = 0,
    Message = 1,
    Postback = 2,
    QuickReply = 3,
    Reaction = 4,
    Attachment = 5,
    Read = 6,
    Delivery = 7,
    Referral = 8,
    OptIn = 9,
    Echo = 10,
    Standby = 11
}
