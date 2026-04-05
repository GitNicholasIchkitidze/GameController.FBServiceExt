using GameController.FBServiceExt.Application.Contracts.Emoji;

namespace GameController.FBServiceExt.Application.Options;

public sealed class MessengerContentOptions
{
    public const string SectionName = "MessengerContent";

    public List<string> ForgetMeTokens { get; set; } = new() { "#forgetme" };

    public string ForgetMeConfirmationPrompt { get; set; } = $"{EmojiBasicConstants.Bell} მონაცემების წაშლის მოთხოვნა\n\nთუ გააგრძელებთ, ჩვენი ბაზიდან წაიშლება თქვენი ისტორიის ჩანაწერები.\n\n⚠️ დარწმუნებული ხართ, რომ გაგრძელება გსურთ?";

    public string ForgetMeConfirmButtonTitle { get; set; } = "✅ კი, წაშალე";

    public string ForgetMeCancelButtonTitle { get; set; } = "↩️ არა, დატოვე";

    public string ForgetMeDeletedText { get; set; } = "✅ მონაცემების წაშლა დასრულდა.\n\nთქვენი ისტორიის ჩანაწერები წარმატებით წაიშალა ჩვენს ბაზაში.";

    public string ForgetMeAlreadyDeletedText { get; set; } = "ℹ️ წასაშლელი ჩანაწერები ვერ მოიძებნა.\n\nთქვენი ისტორიის ჩანაწერები უკვე წაშლილია ან ჩვენს ბაზაში აღარ არსებობს.";

    public string ForgetMeCancelledText { get; set; } = "ℹ️ წაშლის მოთხოვნა გაუქმდა.\n\nთქვენი ისტორიის ჩანაწერები უცვლელად დარჩა.";

    public string VotingInactiveText { get; set; } = "ℹ️ ვოტინგი ამჟამად არ არის აქტიური.";

    public string CandidateVoteButtonTitleFormat { get; set; } = "{DisplayName} 👍";

    public string VoteConfirmationPromptFormat { get; set; } = "დაადასტურეთ თქვენი არჩევანი {Seconds} წამში";

    public string VoteConfirmationCorrectButtonTitleFormat { get; set; } = "{DisplayName}";

    public List<string> VoteConfirmationDecoyButtonTitles { get; set; } = new() { "-", "-" };


    public string VoteConfirmationExpiredText { get; set; } = "⌛ დადასტურების დრო ამოიწურა.";

    public string VoteAcceptedTextFormat { get; set; } = "თქვენ ხმა {CandidateDisplayName}-ს მიეცით. მადლობა. ჩვენ კვლავ მივიღებთ თქვენს ხმას {CooldownUntilLocal}-დან";

    public string CooldownActiveTextFormat { get; set; } = "ბოლო ხმის მიცემიდან არ გასულა {CooldownMinutes} წუთი. ჩვენ მივიღებთ თქვენს ხმას {CooldownUntilLocal}-ის მერე";

    public string CooldownTimeFormat { get; set; } = "HH:mm:ss";
}