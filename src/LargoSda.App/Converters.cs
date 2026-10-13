using Avalonia.Data.Converters;

namespace LargoSda.App;

public static class Converters
{
    public static readonly IValueConverter PageTitle =
        new FuncValueConverter<bool, string>(isCodes => isCodes ? "Аккаунты" : "Подтверждения");

    public static readonly IValueConverter LinkButton =
        new FuncValueConverter<LinkStep, string>(step => step switch
        {
            LinkStep.Choose => "",
            LinkStep.Credentials => "Войти",
            LinkStep.EmailCode => "Продолжить",
            LinkStep.Activation => "Включить Guard",
            _ => "Готово",
        });
}
