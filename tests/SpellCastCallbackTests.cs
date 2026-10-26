#:project ../HiAuRo/HiAuRo.csproj
#:property PublishAot=false
#:property TargetFramework=net10.0-windows

using HiAuRo.Runtime;

Test_只有实际读条GCD触发OnSpellCastSuccess();
Test_瞬发GCD不触发OnSpellCastSuccess();
Test_能力技不触发OnSpellCastSuccess();
Test_执行截止时间();
Console.WriteLine("=== 全部通过 ===");

void Test_只有实际读条GCD触发OnSpellCastSuccess()
{
    if (!SpellCast.ShouldRaiseSpellCastSuccessCallback(isAbility: false, castTimeMs: 1966))
        throw new Exception("非能力技且有读条时间时应触发 OnSpellCastSuccess");
}

void Test_瞬发GCD不触发OnSpellCastSuccess()
{
    if (SpellCast.ShouldRaiseSpellCastSuccessCallback(isAbility: false, castTimeMs: 0))
        throw new Exception("瞬发 GCD 不应触发 OnSpellCastSuccess");
}

void Test_能力技不触发OnSpellCastSuccess()
{
    if (SpellCast.ShouldRaiseSpellCastSuccessCallback(isAbility: true, castTimeMs: 1966))
        throw new Exception("能力技不应触发 OnSpellCastSuccess");
}

void Test_执行截止时间()
{
    if (SpellCast.IsExecutionDeadlineExpired(0, 1000))
        throw new Exception("零截止时间不应超时");
    if (SpellCast.IsExecutionDeadlineExpired(1000, 999))
        throw new Exception("截止时间前不应超时");
    if (!SpellCast.IsExecutionDeadlineExpired(1000, 1000))
        throw new Exception("到达截止时间应超时");
    if (!SpellCast.IsExecutionDeadlineExpired(1000, 1001))
        throw new Exception("超过截止时间应超时");
}
