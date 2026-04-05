namespace GameController.FBServiceExt.Application.Contracts.Emoji;

public static class EmojiBasicConstants
{
	// --- სტატუსი და ინტერფეისი ---
	public const string Success = "✅";          // \u2705 \\ მწვანე მონიშვნა
	public const string Error = "❌";            // \u274C \\ წითელი იქსი
	public const string Warning = "⚠️";         // \u26A0\uFE0F \\ გაფრთხილება
	public const string Info = "ℹ️";            // \u2139\uFE0F \\ ინფორმაცია
	public const string Trash = "🗑️";          // \U0001F5D1\uFE0F \\ წაშლა/ურნა
	public const string Back = "↩️";                // \u21A9\uFE0F \\ უკან დაბრუნება
	public const string Forward = "↪";          // \u21AA\uFE0F \\ წინსვლა/წინ
	public const string Question = "❓"; // \u2753 \\ კითხვის ნიშანი
	public const string FastResponse = "⚡";

	// --- დრო და პროგრესი ---
	public const string Countdown = "⏱️";  // \u23F1\uFE0F \\ წამზომი
	public const string Timer = "⏲️";      // \u23F2\uFE0F \\ ტაიმერი
	public const string Wait = "⏳";       // \u231B \\ მოლოდინი/ქვიშის საათი
	public const string Clock = "🕒";      // \U0001F552 \\ საათი
	public const string Bell = "🔔";       // \U0001F514 \\ ზარი/შეტყობინება

	// --- გეიმინგი და კონკურსანტები ---
	public const string Trophy = "🏆";     // \U0001F3C6 \\ თასი
	public const string Star = "⭐";       // \u2B50 \\ ვარსკვლავი/ქულა
	public const string User = "👤";       // \U0001F464 \\ კონკურსანტი/მომხმარებელი
	public const string MedalGold = "🥇";  // \U0001F947 \\ პირველი ადგილი
	public const string MedalSilver = "🥈"; // \U0001F948 \\ მეორე ადგილი
	public const string MedalBronze = "🥉"; // \U0001F949 \\ მესამე ადგილი
	public const string Diamond = "💎";    // \U0001F48E \\ ბრილიანტი/ბონუსი

	// --- ჟესტები და ემოციები ---
	public const string ThumbsUp = "👍";    // \U0001F44D \\ მოწონება
	public const string OK = "👌";          // \U0001F44C	
	public const string Party = "🥳";      // \U0001F973 \\ ზეიმი/გამარჯვება
	public const string Claps = "👏";      // \U0001F44F \\ ტაში
	public const string Fire = "🔥";       // \U0001F525 \\ პოპულარული/ცხელი

	// --- სტრიმინგი და მედია ---
	public const string Live = "🔴";       // \U0001F534 \\ ლაივი/ჩაწერა
	public const string Micro = "🎤";      // \U0001F3A4 \\ მიკროფონი
	public const string Speaker = "📢";    // \U0001F4E2 \\ გამოცხადება

	// --- ჯოკერები / დახმარებები ---
	public const string FiftyFifty = "🌓";
	public const string Audience = "👥";
	public const string PhoneFriend = "📞";

	// --- სტატისტიკა და შედეგები ---
	public const string BarChart = "📊";       // შედეგების გრაფიკი
	public const string ChartUp = "📈";        // პოპულარული პასუხი
	public const string PieChart = "🥧";       // პროცენტული განაწილება
	public const string CheckMark = "🗳️";      // არჩევანი დაფიქსირებულია
	public const string Majority = "👥";       // უმრავლესობის აზრი

	// --- რეაქციები შედეგებზე ---
	public const string Agreement = "🤝";      // როცა დარბაზი და მოთამაშე თანხმდებიან
	public const string Conflict = "⚔️";       // როცა აზრები გაიყო
	public const string Surprise = "😲";       // მოულოდნელი შედეგი

	// --- ვალიდაცია და სტატუსი (Validation & Status) ---
	public const string HollowCircle = "⭕";    // U+2B55 - წითელი ცარიელი წრე (პროცესშია)
	public const string CheckMarkButton = "✅"; // U+2705 - დადასტურების ღილაკი
	public const string CheckBox = "☑️";       // U+2611 - მონიშნული ჩამკეტი (Check box)
	public const string SimpleCheck = "✔";      // U+2714 - მარტივი "სწორია" (Tick)
	public const string CrossMark = "❌";      // U+274C - წითელი ჯვარი (შეცდომა)
	public const string CrossMarkButton = "❎"; // U+274E - უარყოფის კვადრატული ღილაკი

	// --- გრაფიკული ელემენტები (Graphic Elements) ---
	public const string CurlyLoop = "➰";        // U+27B0 - მარყუჟი (ციკლი)
	public const string DoubleCurlyLoop = "➿";  // U+27BF - ორმაგი მარყუჟი
	public const string Alternation = "〽️";      // U+303D - ნაწილობრივი მონაცვლეობის ნიშანი
	public const string Asterisk8 = "✳️";       // U+2733 - რვაქიმიანი ფიფქი
	public const string Star8 = "✴️";           // U+2734 - რვაქიმიანი ვარსკვლავი
	public const string Sparkle = "❇️";         // U+2747 - ნაპერწკალი (აქცენტი)

	// --- ციფრები / კლავიშები (Keycaps) ---
	public const string KeycapHash = "#️⃣";     // U+0023 U+FE0F U+20E3 - დიეზი
	public const string KeycapStar = "*️⃣";     // U+002A U+FE0F U+20E3 - ფიფქი
	public const string Key0 = "0️⃣";            // U+0030 U+FE0F U+20E3 - ციფრი 0
	public const string Key1 = "1️⃣";            // U+0031 U+FE0F U+20E3 - ციფრი 1
	public const string Key2 = "2️⃣";            // U+0032 U+FE0F U+20E3 - ციფრი 2
	public const string Key3 = "3️⃣";            // U+0033 U+FE0F U+20E3 - ციფრი 3
	public const string Key4 = "4️⃣";            // U+0034 U+FE0F U+20E3 - ციფრი 4
	public const string Key5 = "5️⃣";            // U+0035 U+FE0F U+20E3 - ციფრი 5
	public const string Key6 = "6️⃣";            // U+0036 U+FE0F U+20E3 - ციფრი 6
	public const string Key7 = "7️⃣";            // U+0037 U+FE0F U+20E3 - ციფრი 7
	public const string Key8 = "8️⃣";            // U+0038 U+FE0F U+20E3 - ციფრი 8
	public const string Key9 = "9️⃣";            // U+0039 U+FE0F U+20E3 - ციფრი 9
	public const string Key10 = "🔟";           // U+1F51F - რიცხვი 10

	// --- სიმბოლოები და ბრენდინგი ---
	public const string Copyright = "©️";        // U+00A9 - საავტორო უფლება
	public const string Registered = "®️";       // U+00AE - რეგისტრირებული ნიშანი
	public const string TradeMark = "™️";        // U+2122 - სავაჭრო ნიშანი
	public const string Splatter = "🫟";        // U+1FADF - ლაქა / შხეფები


	public const string FlagGeorgia = "🇬🇪";       // U+1F1EC U+1F1EA - საქართველოს დროშა
}
