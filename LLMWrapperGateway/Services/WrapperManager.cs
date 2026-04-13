using LLMWrapperGateway.Data;
using LLMWrapperGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace LLMWrapperGateway.Services;

public class WrapperManager
{
    private readonly WrapperDbContext _db;

    public WrapperManager(WrapperDbContext db)
    {
        _db = db;
    }

    public async Task<WrapperConfig> CreateAsync(CreateWrapperRequest request)
    {
        var wrapper = new WrapperConfig
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Provider = request.Provider,
            BaseUrl = request.BaseUrl.TrimEnd('/'),
            Session = request.Session,
            RequestMapping = request.RequestMapping,
            ResponsePath = request.ResponsePath,
            CreatedAt = DateTime.UtcNow
        };

        _db.Wrappers.Add(wrapper);
        await _db.SaveChangesAsync();
        return wrapper;
    }

    public async Task<List<WrapperConfig>> ListAsync()
    {
        return await _db.Wrappers.OrderByDescending(w => w.CreatedAt).ToListAsync();
    }

    public async Task<WrapperConfig?> GetByIdAsync(Guid id)
    {
        return await _db.Wrappers.FindAsync(id);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var wrapper = await _db.Wrappers.FindAsync(id);
        if (wrapper is null) return false;
        _db.Wrappers.Remove(wrapper);
        await _db.SaveChangesAsync();
        return true;
    }
}
