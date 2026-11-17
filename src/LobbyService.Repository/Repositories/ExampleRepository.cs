using LobbyService.Domain.Entities;
using LobbyService.Repository.Data;
using LobbyService.Repository.Interfaces;

namespace LobbyService.Repository.Repositories;

public class ExampleRepository(AppDbContext context) : Repository<Example>(context), IExampleRepository
{
}
