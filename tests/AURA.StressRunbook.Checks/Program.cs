// This entry point intentionally cannot certify a live system from source strings.
Console.Error.WriteLine("BLOCKED: run scripts/dev/Test-BatchInfrastructureStress.ps1 with real images, Firebase identity and explicit containers; Test-RabbitMqScaling.ps1 checks live consumers. See tests/AURA.M1.RegressionTests for payment/retry regressions.");
return 2;
