using EKA.Application.Common;
using Shouldly;

namespace EKA.Application.Tests.Common;

/// <summary>
/// The contract every handler's return type depends on.
/// </summary>
public sealed class ResultTests
{
    private static readonly Error SampleError =
        Error.NotFound("document.not_found", "No such document.");

    [Fact]
    public void A_success_carries_its_value()
    {
        var result = Result<int>.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Reading_the_value_of_a_failure_throws_instead_of_returning_null()
    {
        var result = Result<string>.Failure(SampleError);

        // Returning default here would surface as a NullReferenceException several frames
        // away, with nothing left pointing at the branch that was never checked.
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Reading_the_error_of_a_success_throws()
    {
        var result = Result<int>.Success(1);

        Should.Throw<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void A_default_result_refuses_to_pretend_it_is_a_failure()
    {
        // C# gives every struct an implicit parameterless constructor, so this compiles and
        // bypasses both factory methods. It is neither a success nor a well-formed failure.
        var result = default(Result<int>);

        result.IsSuccess.ShouldBeFalse();

        var exception = Should.Throw<InvalidOperationException>(() => result.Error);

        // The message names the actual cause, so whoever hits this is not left hunting for
        // a missing error that was never assigned.
        exception.Message.ShouldContain("default");
    }

    [Fact]
    public void A_default_non_generic_result_behaves_the_same_way()
    {
        var result = default(Result);

        result.IsSuccess.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void A_value_converts_implicitly_to_a_success()
    {
        Result<string> result = "done";

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("done");
    }

    [Fact]
    public void An_error_converts_implicitly_to_a_failure()
    {
        Result<string> result = SampleError;

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SampleError);
    }

    [Fact]
    public void A_null_error_is_refused_rather_than_producing_a_failure_with_nothing_to_report()
    {
        Should.Throw<ArgumentNullException>(() => Result<int>.Failure(null!));
        Should.Throw<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Theory]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.Provider)]
    public void Every_error_type_has_a_declared_numeric_value(ErrorType type)
    {
        // These end up in logs and metric labels. An enum relying on declaration order
        // renumbers itself the day somebody inserts a member, and historical data silently
        // starts meaning something else.
        ((int)type).ShouldBeGreaterThan(0);
        Enum.IsDefined(type).ShouldBeTrue();
    }

    [Fact]
    public void Error_factories_set_the_matching_type()
    {
        Error.Validation("a", "b").Type.ShouldBe(ErrorType.Validation);
        Error.NotFound("a", "b").Type.ShouldBe(ErrorType.NotFound);
        Error.Conflict("a", "b").Type.ShouldBe(ErrorType.Conflict);
        Error.Forbidden("a", "b").Type.ShouldBe(ErrorType.Forbidden);
        Error.Provider("a", "b").Type.ShouldBe(ErrorType.Provider);
    }
}
