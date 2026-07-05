using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Data;

/// <summary>
/// Factory necessária para que as ferramentas do Entity Framework (como Migrations) 
/// consigam instanciar o DbContext corretamente agora que ele exige o ITenantService no construtor.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        
        // Define a mesma fonte de dados utilizada no Program.cs
        optionsBuilder.UseSqlite("Data Source=agendamentos.db");

        // Em tempo de design, fornecemos uma implementação dummy do ITenantService
        // apenas para satisfazer a dependência do construtor do AppDbContext.
        return new AppDbContext(optionsBuilder.Options, new DesignTimeTenantService());
    }

    private class DesignTimeTenantService : ITenantService
    {
        // Retorna um ID padrão (1) para que as migrações possam ser processadas
        public int GetTenantId() => 1;

        public bool IsSuperAdmin() => false;

        // Implementação vazia, pois não há troca de tenant durante migrações
        public void SetTenantId(int tenantId) { }
    }
}