#region

using System;
using System.Linq;
using System.Text.RegularExpressions;
using NebulaModel.DataStructures.Chat;
using UnityEngine;

#endregion

namespace NebulaModel.Utils;

public static class ChatUtils
{
    private static readonly string[] AllowedTags =
    {
        "b", "i", "s", "u", "indent", "link", "mark", "sprite", "sub", "sup", "color"
    };

    public static string SanitizeText(string input)
    {
        // Matches any valid rich text tag. For example: <sprite name="hello" index=5>
        var regex = new Regex("""<([/\w]+)=?["#]?\w*"?\s?[\s\w"=]*>""");

        return regex.Replace(input, match =>
        {
            var tagName = match.Groups[1].Value;
            if (AllowedTags.Contains(tagName) || AllowedTags.Contains(tagName.Substring(1)))
            {
                return match.Value;
            }
            return "";
        });
    }

    public static Color GetMessageColor(ChatMessageType messageType)
    {
        return messageType switch
        {
            ChatMessageType.PlayerMessage => Color.white,
            ChatMessageType.SystemInfoMessage => Color.cyan,
            ChatMessageType.SystemWarnMessage => new Color(1, 0.95f, 0, 1),
            ChatMessageType.BattleMessage => Color.cyan,
            ChatMessageType.CommandUsageMessage => new Color(1, 0.65f, 0, 1),
            ChatMessageType.CommandOutputMessage => new Color(0.8f, 0.8f, 0.8f, 1),
            ChatMessageType.CommandErrorMessage => Color.red,
            ChatMessageType.PlayerMessagePrivate => Color.green,
            _ => Color.white, // Default chat color is white
        };
    }

    public static bool IsPlayerMessage(this ChatMessageType type)
    {
        return type is ChatMessageType.PlayerMessage or ChatMessageType.PlayerMessagePrivate;
    }

    public static bool IsCommandMessage(this ChatMessageType type)
    {
        return type is ChatMessageType.CommandUsageMessage or ChatMessageType.CommandOutputMessage or ChatMessageType.CommandErrorMessage;
    }

    public static bool Contains(this string source, string toCheck, StringComparison comp)
    {
        return source?.IndexOf(toCheck, comp) >= 0;
    }

    public static string FormatMessage(RawChatMessage message)
    {
        var formattedString = "";

        if (!string.IsNullOrEmpty(message.UserName))
        {
            formattedString = message.UserName + " : ";
        }

        if (!IsCommandMessage(message.MessageType))
        {
            formattedString = $"[{message.Timestamp:HH:mm}] " + formattedString;
        }

        return formattedString + message.MessageText;
    }
}
