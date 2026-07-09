using System.Net.Mail;

namespace AuthService.Auth;

public static class AccountValidation
{
    public static string? ValidateEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "Email is required.";
        }

        var trimmedEmail = email.Trim();

        if (trimmedEmail.Length > 320)
        {
            return "Email is too long.";
        }

        try
        {
            _ = new MailAddress(trimmedEmail);
            return null;
        }
        catch (FormatException)
        {
            return "Email is invalid.";
        }
    }

    public static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "Username is required.";
        }

        var trimmedUsername = username.Trim();

        if (trimmedUsername.Length is < 3 or > 32)
        {
            return "Username must be between 3 and 32 characters.";
        }

        if (!trimmedUsername.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
        {
            return "Username can only contain letters, numbers, and underscores.";
        }

        return null;
    }

    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return "Password is required.";
        }

        if (password.Length is < 8 or > 128)
        {
            return "Password must be between 8 and 128 characters.";
        }

        return null;
    }
}

