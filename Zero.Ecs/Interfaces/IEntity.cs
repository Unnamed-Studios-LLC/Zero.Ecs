namespace Zero.Ecs
{
    public interface IEntity
    {
        Entities Entities { get; }
        uint EntityId { get; }
    }
}
