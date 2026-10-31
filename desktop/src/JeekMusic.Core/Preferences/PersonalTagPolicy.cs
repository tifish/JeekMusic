namespace JeekMusic.Core.Preferences;

/// <summary>Independent heads; a score is a margin, not a calibrated probability.</summary>
public static class PersonalTagPolicy
{
    public sealed record Result(string? Value,string Source,string? Abstention);
    public static Result Resolve(string dimension,string? publicValue,string? personalValue,bool validatedEnabled,
        string? manualValue=null,bool classicalTitle=false)
    {
        if(!PreferenceProtocol.Dimensions.Contains(dimension)) throw new ArgumentException("Unknown dimension");
        if(manualValue!=null) return new(manualValue,"MANUAL",null);
        if(dimension=="GENRE" && classicalTitle) return new("CLASSICAL","TITLE_RULE",null);
        if(validatedEnabled && personalValue!=null && PreferenceProtocol.Binary(personalValue,dimension)) return new(personalValue,"PERSONAL",null);
        return new(publicValue,"PUBLIC_AUTO",validatedEnabled?"head_abstained":"head_not_validated_or_enabled");
    }
}
