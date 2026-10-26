using YR.FrameWork.DependencyInjection;
using Mapster;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yale.DAL;
using Yale.Helper;
using Yale.Model;

namespace Yale.BLL
{
    public class AuthManager : IScopedSelfDependency
    {
        private readonly AuthService _authService;
        public AuthManager(AuthService authService)
        {
            _authService = authService;
        }

        public async Task<BaseResult> UpdateAuthAsync(UpdateAuthDto request)
        {
            var entity = request.Adapt<AuthEntity>();

            var existing = await _authService.GetByOneAsync(c => c.Role == request.Role);
            if (existing == null)
            {
                return new BaseResult() { Status = SystemEnums.Result.Fail, Msg = "权限记录不存在，请重启程序初始化" };
            }
            entity.Id = existing.Id;

            var res = await _authService.UpdateAsync(entity);

            if (res)
            {
                return new BaseResult() { Status = SystemEnums.Result.Success };
            }
            return new BaseResult() { Status = SystemEnums.Result.Fail, Msg = "更新权限失败" };
        }

        public async Task<BaseResult<QueryAuthResultDto>> GetAuthAsync(QueryAuthDto request)
        {
            var res = await _authService.GetByOneAsync(c => c.Role == request.Role);

            if (res == null)
            {
                return new BaseResult<QueryAuthResultDto>() { Status = SystemEnums.Result.Fail, Msg = "角色权限未配置" };
            }

            return new BaseResult<QueryAuthResultDto>() { Status = SystemEnums.Result.Success, Data = new List<QueryAuthResultDto>() { res.Adapt<QueryAuthResultDto>() } };

        }
    }
}
