using FluentValidation;

namespace KnowledgeHub.Application.Identity;

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator(ICommonPasswords common)
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254).EmailAddress().WithMessage("Must be a valid email address.");
        RuleFor(x => x.Password)
            .NotEmpty()
            .Length(12, 128).WithMessage("Must be 12 to 128 characters.")
            .Must(p => !common.IsCommon(p)).WithMessage("This password is too common.");
        RuleFor(x => x.DisplayName).Must(n => n is { } s && s.Trim().Length is >= 1 and <= 60)
            .WithMessage("Must be 1 to 60 characters.");
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class PasswordResetRequestValidator : AbstractValidator<PasswordResetRequest>
{
    public PasswordResetRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254).EmailAddress().WithMessage("Must be a valid email address.");
    }
}

public sealed class NewPasswordRules : AbstractValidator<string>
{
    public NewPasswordRules(ICommonPasswords common)
    {
        RuleFor(p => p).NotEmpty()
            .Length(12, 128).WithMessage("Must be 12 to 128 characters.")
            .Must(p => !common.IsCommon(p)).WithMessage("This password is too common.");
    }
}

public sealed class PasswordResetConfirmValidator : AbstractValidator<PasswordResetConfirmRequest>
{
    public PasswordResetConfirmValidator(ICommonPasswords common)
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NewPassword).SetValidator(new NewPasswordRules(common));
    }
}

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordValidator(ICommonPasswords common)
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(128);
        RuleFor(x => x.NewPassword).SetValidator(new NewPasswordRules(common));
    }
}

public sealed class UpdateMeValidator : AbstractValidator<UpdateMeRequest>
{
    public UpdateMeValidator()
    {
        RuleFor(x => x.DisplayName)
            .Must(n => n is null || n.Trim().Length is >= 1 and <= 60)
            .WithMessage("Must be 1 to 60 characters.");
    }
}
