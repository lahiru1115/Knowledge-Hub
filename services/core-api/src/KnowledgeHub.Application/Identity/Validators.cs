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
