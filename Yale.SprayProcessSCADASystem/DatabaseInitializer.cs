using SqlSugar;
using Yale.Model;

namespace Yale.SprayProcessSCADASystem;

internal static class DatabaseInitializer
{
    public static void Initialize(ISqlSugarClient db)
    {
        // A fresh output directory must work in Debug and Release builds alike.
        db.CodeFirst.SetStringDefaultLength(200)
            .InitTables(typeof(AuthEntity), typeof(DataEntity), typeof(RecipeEntity), typeof(UserEntity));

        AddRole(db, "工程师", control: true, recipe: true, parameters: true);
        AddRole(db, "操作员", control: true, recipe: true, parameters: false);
        AddRole(db, "访客", control: false, recipe: false, parameters: false);
    }

    private static void AddRole(ISqlSugarClient db, string role, bool control, bool recipe, bool parameters)
    {
        if (db.Queryable<AuthEntity>().Any(x => x.Role == role)) return;
        db.Insertable(new AuthEntity
        {
            Role = role,
            ControlModule = control,
            MonitorModule = true,
            RecipeModule = recipe,
            LogModule = true,
            ReportModule = true,
            ChartModule = true,
            ParamModule = parameters,
            UserModule = false
        }).ExecuteCommand();
    }
}
