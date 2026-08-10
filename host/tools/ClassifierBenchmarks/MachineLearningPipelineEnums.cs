using System;
using System.Text.Json.Serialization;

namespace ClassifierBenchmarks;

#region ML & Cloud Pipeline Enums (221-250)

/// <summary>
/// Mathematical loss functions used for model training and evaluation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LossFunctionKind
{
    CrossEntropy,
    MeanSquaredError,
    MeanAbsoluteError,
    FocalLoss,
    BinaryCrossEntropy,
    HuberLoss,
    TripletMarginLoss,
    CosineEmbeddingLoss,
    DiceLoss,
    ContrastiveLoss
}

/// <summary>
/// Optimization algorithms used for gradient descent parameter updates.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OptimizationAlgorithmKind
{
    Adam,
    AdamW,
    SGD,
    RMSprop,
    AdaGrad,
    AdaDelta,
    NAdam,
    LBFGS,
    Lion
}

/// <summary>
/// Learning rate decay and scheduling strategies.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LearningRateSchedulerKind
{
    CosineAnnealing,
    StepLR,
    ExponentialLR,
    ReduceLROnPlateau,
    LinearWarmup,
    PolynomialLR,
    CyclicLR,
    Constant
}

/// <summary>
/// Neural network and machine learning model architectural families.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelArchitectureType
{
    Transformer,
    ConvolutionalNeuralNetwork,
    RecurrentNeuralNetwork,
    DiffusionModel,
    Autoencoder,
    MixtureOfExperts,
    StateSpaceModel,
    GraphNeuralNetwork,
    MLP
}

/// <summary>
/// Text tokenization algorithms for preparing text inputs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TokenizerTypeKind
{
    BytePairEncoding,
    WordPiece,
    SentencePiece,
    Unigram,
    CharacterLevel,
    Subword,
    ByteLevel
}

/// <summary>
/// Dense vector embedding model architectures and variants.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EmbeddingModelKind
{
    TextEmbeddingAda002,
    TextEmbedding3Small,
    TextEmbedding3Large,
    E5Base,
    BgeLarge,
    MiniLM,
    NomicEmbed,
    CustomBert
}

/// <summary>
/// Vector similarity and distance calculation metrics.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VectorDistanceMetric
{
    Cosine,
    Euclidean,
    DotProduct,
    Manhattan,
    Chebyshev,
    Hamming,
    Mahalanobis
}

/// <summary>
/// Structured prompt engineering design patterns.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromptStrategyPattern
{
    ZeroShot,
    FewShot,
    ChainOfThought,
    TreeOfThoughts,
    ReAct,
    SelfConsistency,
    DirectionalStimulus,
    SkeletonOfThought,
    SystemPromptOnly
}

/// <summary>
/// Model parameter quantization precision levels.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuantizationPrecision
{
    Float32,
    Float16,
    BFloat16,
    Int8,
    Int4,
    Gptq4Bit,
    Awq4Bit,
    GgmlQ4_0,
    GgmlQ8_0,
    FP8
}

/// <summary>
/// Parameter-efficient and full fine-tuning methodologies.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FineTuningMethodKind
{
    FullFineTuning,
    LoRA,
    QLoRA,
    PrefixTuning,
    PromptTuning,
    AdapterLayers,
    DPO,
    PPO,
    RLHF
}

/// <summary>
/// Partition splits for machine learning dataset evaluation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatasetSplitKind
{
    Train,
    Validation,
    Test,
    Holdout,
    CrossValidationFold,
    Calibration
}

/// <summary>
/// Classification outcome quadrants in a confusion matrix.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConfusionMatrixQuadrant
{
    TruePositive,
    FalsePositive,
    TrueNegative,
    FalseNegative
}

/// <summary>
/// Statistical score metrics for model receiver operating characteristics.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoCScoreType
{
    AucRoc,
    PrecisionRecallAuc,
    LogLoss,
    GMean,
    YoudensJ,
    F2Score,
    F0_5Score
}

/// <summary>
/// Synthetic training data generation techniques.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyntheticDataGenerator
{
    LlmAugmentation,
    GAN,
    VariationalAutoencoder,
    RuleBasedMutation,
    BackTranslation,
    OversamplingSMOTE
}

/// <summary>
/// Metrics for measuring data distribution and concept drift.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelDriftMetric
{
    PopulationStabilityIndex,
    WassersteinDistance,
    KolmogorovSmirnovTest,
    KullbackLeiblerDivergence,
    JensenShannonDivergence,
    ConceptDriftRate
}

/// <summary>
/// Feature attribution and explainability methods.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeatureImportanceKind
{
    ShapleyValues,
    IntegratedGradients,
    PermutationImportance,
    GiniImpurity,
    Gain,
    AttentionWeights
}

/// <summary>
/// Lifecycle status states in a model registry repository.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelRegistryStatus
{
    Draft,
    InReview,
    Approved,
    Staging,
    Production,
    Deprecated,
    Archived,
    Rejected
}

/// <summary>
/// Search strategies for hyperparameter optimization runs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HyperparameterSearchStrategy
{
    GridSearch,
    RandomSearch,
    BayesianOptimization,
    Hyperband,
    TreeStructuredParzen,
    EvolutionaryAlgorithm
}

/// <summary>
/// Percentile brackets for inference latency measurement.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InferenceLatencyPercentile
{
    P50,
    P75,
    P90,
    P95,
    P99,
    P99_9,
    Mean,
    Max
}

/// <summary>
/// Quality and policy validation gates for AI model outputs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiModelGateType
{
    SafetyFilter,
    QualityThreshold,
    CostBudget,
    LatencySla,
    ContentModeration,
    HallucinationDetector
}

/// <summary>
/// Cloud infrastructure provider platforms.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CloudProviderKind
{
    Aws,
    Azure,
    Gcp,
    Cloudflare,
    DigitalOcean,
    OracleCloud,
    OnPremise,
    Hybrid
}

/// <summary>
/// Container runtime engines for model deployment.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContainerEngineKind
{
    Docker,
    Podman,
    Containerd,
    CriO,
    Singularity
}

/// <summary>
/// Container orchestration management platforms.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrchestratorKind
{
    Kubernetes,
    DockerSwarm,
    Nomad,
    AmazonEcs,
    AzureAks,
    GoogleGke
}

/// <summary>
/// Release and deployment strategy methodologies.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeploymentStrategyKind
{
    RollingUpdate,
    BlueGreen,
    Canary,
    Recreate,
    ATesting,
    Shadow
}

/// <summary>
/// Service mesh proxy and networking protocols.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceMeshProtocol
{
    Istio,
    Linkerd,
    Consul,
    Envoy,
    Traefik,
    OpenTelemetry
}

/// <summary>
/// Traffic distribution algorithms for load balancers.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoadBalancerAlgorithm
{
    RoundRobin,
    LeastConnections,
    IPHash,
    WeightedRoundRobin,
    RandomChoice,
    LeastResponseTime
}

/// <summary>
/// Metrics driving compute autoscaling rules.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutoScalingMetricKind
{
    CpuUtilization,
    MemoryUtilization,
    RequestRate,
    QueueLength,
    CustomMetric,
    CpuAndMemoryCombined
}

/// <summary>
/// Roles assigned to nodes within a compute cluster.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClusterNodeRole
{
    ControlPlane,
    Worker,
    IngressGateway,
    StorageNode,
    AiAccelerator,
    EdgeNode
}

/// <summary>
/// Backends for secure credentials and secrets management.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecretStorageBackend
{
    HashiCorpVault,
    AwsSecretsManager,
    AzureKeyVault,
    GoogleSecretManager,
    KubernetesSecret,
    EnvironmentVariable
}

/// <summary>
/// Content Delivery Network caching rules and strategies.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CdnCacheStrategy
{
    CacheEverything,
    BypassCache,
    CacheStaticOnly,
    StaleWhileRevalidate,
    CacheByQueryString,
    EdgeSideIncludes
}

#endregion

#region Extension Methods

/// <summary>
/// Extension methods for Machine Learning Pipeline enums (221-250).
/// </summary>
public static class MachineLearningPipelineEnumExtensions
{
    public static string ToApiString(this LossFunctionKind val) => val switch
    {
        LossFunctionKind.CrossEntropy => "cross_entropy",
        LossFunctionKind.MeanSquaredError => "mean_squared_error",
        LossFunctionKind.MeanAbsoluteError => "mean_absolute_error",
        LossFunctionKind.FocalLoss => "focal_loss",
        LossFunctionKind.BinaryCrossEntropy => "binary_cross_entropy",
        LossFunctionKind.HuberLoss => "huber_loss",
        LossFunctionKind.TripletMarginLoss => "triplet_margin_loss",
        LossFunctionKind.CosineEmbeddingLoss => "cosine_embedding_loss",
        LossFunctionKind.DiceLoss => "dice_loss",
        LossFunctionKind.ContrastiveLoss => "contrastive_loss",
        _ => "cross_entropy"
    };

    public static LossFunctionKind ParseLossFunctionKind(string? s, LossFunctionKind defaultValue = LossFunctionKind.CrossEntropy)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "cross_entropy" or "crossentropy" or "ce" => LossFunctionKind.CrossEntropy,
            "mean_squared_error" or "mse" => LossFunctionKind.MeanSquaredError,
            "mean_absolute_error" or "mae" => LossFunctionKind.MeanAbsoluteError,
            "focal_loss" or "focal" => LossFunctionKind.FocalLoss,
            "binary_cross_entropy" or "bce" => LossFunctionKind.BinaryCrossEntropy,
            "huber_loss" or "huber" => LossFunctionKind.HuberLoss,
            "triplet_margin_loss" or "triplet" => LossFunctionKind.TripletMarginLoss,
            "cosine_embedding_loss" or "cosine" => LossFunctionKind.CosineEmbeddingLoss,
            "dice_loss" or "dice" => LossFunctionKind.DiceLoss,
            "contrastive_loss" or "contrastive" => LossFunctionKind.ContrastiveLoss,
            _ => Enum.TryParse<LossFunctionKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this OptimizationAlgorithmKind val) => val switch
    {
        OptimizationAlgorithmKind.Adam => "adam",
        OptimizationAlgorithmKind.AdamW => "adam_w",
        OptimizationAlgorithmKind.SGD => "sgd",
        OptimizationAlgorithmKind.RMSprop => "rms_prop",
        OptimizationAlgorithmKind.AdaGrad => "ada_grad",
        OptimizationAlgorithmKind.AdaDelta => "ada_delta",
        OptimizationAlgorithmKind.NAdam => "n_adam",
        OptimizationAlgorithmKind.LBFGS => "lbfgs",
        OptimizationAlgorithmKind.Lion => "lion",
        _ => "adam"
    };

    public static OptimizationAlgorithmKind ParseOptimizationAlgorithmKind(string? s, OptimizationAlgorithmKind defaultValue = OptimizationAlgorithmKind.Adam)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "adam" => OptimizationAlgorithmKind.Adam,
            "adam_w" or "adamw" => OptimizationAlgorithmKind.AdamW,
            "sgd" => OptimizationAlgorithmKind.SGD,
            "rms_prop" or "rmsprop" => OptimizationAlgorithmKind.RMSprop,
            "ada_grad" or "adagrad" => OptimizationAlgorithmKind.AdaGrad,
            "ada_delta" or "adadelta" => OptimizationAlgorithmKind.AdaDelta,
            "n_adam" or "nadam" => OptimizationAlgorithmKind.NAdam,
            "lbfgs" => OptimizationAlgorithmKind.LBFGS,
            "lion" => OptimizationAlgorithmKind.Lion,
            _ => Enum.TryParse<OptimizationAlgorithmKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this LearningRateSchedulerKind val) => val switch
    {
        LearningRateSchedulerKind.CosineAnnealing => "cosine_annealing",
        LearningRateSchedulerKind.StepLR => "step_lr",
        LearningRateSchedulerKind.ExponentialLR => "exponential_lr",
        LearningRateSchedulerKind.ReduceLROnPlateau => "reduce_lr_on_plateau",
        LearningRateSchedulerKind.LinearWarmup => "linear_warmup",
        LearningRateSchedulerKind.PolynomialLR => "polynomial_lr",
        LearningRateSchedulerKind.CyclicLR => "cyclic_lr",
        LearningRateSchedulerKind.Constant => "constant",
        _ => "cosine_annealing"
    };

    public static LearningRateSchedulerKind ParseLearningRateSchedulerKind(string? s, LearningRateSchedulerKind defaultValue = LearningRateSchedulerKind.CosineAnnealing)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "cosine_annealing" or "cosine" => LearningRateSchedulerKind.CosineAnnealing,
            "step_lr" or "step" => LearningRateSchedulerKind.StepLR,
            "exponential_lr" or "exponential" => LearningRateSchedulerKind.ExponentialLR,
            "reduce_lr_on_plateau" or "plateau" => LearningRateSchedulerKind.ReduceLROnPlateau,
            "linear_warmup" or "warmup" => LearningRateSchedulerKind.LinearWarmup,
            "polynomial_lr" or "polynomial" => LearningRateSchedulerKind.PolynomialLR,
            "cyclic_lr" or "cyclic" => LearningRateSchedulerKind.CyclicLR,
            "constant" => LearningRateSchedulerKind.Constant,
            _ => Enum.TryParse<LearningRateSchedulerKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this ModelArchitectureType val) => val switch
    {
        ModelArchitectureType.Transformer => "transformer",
        ModelArchitectureType.ConvolutionalNeuralNetwork => "cnn",
        ModelArchitectureType.RecurrentNeuralNetwork => "rnn",
        ModelArchitectureType.DiffusionModel => "diffusion",
        ModelArchitectureType.Autoencoder => "autoencoder",
        ModelArchitectureType.MixtureOfExperts => "mixture_of_experts",
        ModelArchitectureType.StateSpaceModel => "state_space_model",
        ModelArchitectureType.GraphNeuralNetwork => "gnn",
        ModelArchitectureType.MLP => "mlp",
        _ => "transformer"
    };

    public static ModelArchitectureType ParseModelArchitectureType(string? s, ModelArchitectureType defaultValue = ModelArchitectureType.Transformer)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "transformer" => ModelArchitectureType.Transformer,
            "cnn" or "convolutional_neural_network" => ModelArchitectureType.ConvolutionalNeuralNetwork,
            "rnn" or "recurrent_neural_network" => ModelArchitectureType.RecurrentNeuralNetwork,
            "diffusion" or "diffusion_model" => ModelArchitectureType.DiffusionModel,
            "autoencoder" => ModelArchitectureType.Autoencoder,
            "mixture_of_experts" or "moe" => ModelArchitectureType.MixtureOfExperts,
            "state_space_model" or "ssm" or "mamba" => ModelArchitectureType.StateSpaceModel,
            "gnn" or "graph_neural_network" => ModelArchitectureType.GraphNeuralNetwork,
            "mlp" => ModelArchitectureType.MLP,
            _ => Enum.TryParse<ModelArchitectureType>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this TokenizerTypeKind val) => val switch
    {
        TokenizerTypeKind.BytePairEncoding => "bpe",
        TokenizerTypeKind.WordPiece => "wordpiece",
        TokenizerTypeKind.SentencePiece => "sentencepiece",
        TokenizerTypeKind.Unigram => "unigram",
        TokenizerTypeKind.CharacterLevel => "character_level",
        TokenizerTypeKind.Subword => "subword",
        TokenizerTypeKind.ByteLevel => "byte_level",
        _ => "bpe"
    };

    public static TokenizerTypeKind ParseTokenizerTypeKind(string? s, TokenizerTypeKind defaultValue = TokenizerTypeKind.BytePairEncoding)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "bpe" or "byte_pair_encoding" => TokenizerTypeKind.BytePairEncoding,
            "wordpiece" => TokenizerTypeKind.WordPiece,
            "sentencepiece" => TokenizerTypeKind.SentencePiece,
            "unigram" => TokenizerTypeKind.Unigram,
            "character_level" or "character" => TokenizerTypeKind.CharacterLevel,
            "subword" => TokenizerTypeKind.Subword,
            "byte_level" or "byte" => TokenizerTypeKind.ByteLevel,
            _ => Enum.TryParse<TokenizerTypeKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this EmbeddingModelKind val) => val switch
    {
        EmbeddingModelKind.TextEmbeddingAda002 => "text_embedding_ada_002",
        EmbeddingModelKind.TextEmbedding3Small => "text_embedding_3_small",
        EmbeddingModelKind.TextEmbedding3Large => "text_embedding_3_large",
        EmbeddingModelKind.E5Base => "e5_base",
        EmbeddingModelKind.BgeLarge => "bge_large",
        EmbeddingModelKind.MiniLM => "minilm",
        EmbeddingModelKind.NomicEmbed => "nomic_embed",
        EmbeddingModelKind.CustomBert => "custom_bert",
        _ => "text_embedding_3_small"
    };

    public static EmbeddingModelKind ParseEmbeddingModelKind(string? s, EmbeddingModelKind defaultValue = EmbeddingModelKind.TextEmbedding3Small)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "text_embedding_ada_002" or "ada_002" => EmbeddingModelKind.TextEmbeddingAda002,
            "text_embedding_3_small" or "text_3_small" => EmbeddingModelKind.TextEmbedding3Small,
            "text_embedding_3_large" or "text_3_large" => EmbeddingModelKind.TextEmbedding3Large,
            "e5_base" or "e5" => EmbeddingModelKind.E5Base,
            "bge_large" or "bge" => EmbeddingModelKind.BgeLarge,
            "minilm" => EmbeddingModelKind.MiniLM,
            "nomic_embed" or "nomic" => EmbeddingModelKind.NomicEmbed,
            "custom_bert" or "bert" => EmbeddingModelKind.CustomBert,
            _ => Enum.TryParse<EmbeddingModelKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this VectorDistanceMetric val) => val switch
    {
        VectorDistanceMetric.Cosine => "cosine",
        VectorDistanceMetric.Euclidean => "euclidean",
        VectorDistanceMetric.DotProduct => "dot_product",
        VectorDistanceMetric.Manhattan => "manhattan",
        VectorDistanceMetric.Chebyshev => "chebyshev",
        VectorDistanceMetric.Hamming => "hamming",
        VectorDistanceMetric.Mahalanobis => "mahalanobis",
        _ => "cosine"
    };

    public static VectorDistanceMetric ParseVectorDistanceMetric(string? s, VectorDistanceMetric defaultValue = VectorDistanceMetric.Cosine)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "cosine" => VectorDistanceMetric.Cosine,
            "euclidean" or "l2" => VectorDistanceMetric.Euclidean,
            "dot_product" or "dot" or "inner_product" => VectorDistanceMetric.DotProduct,
            "manhattan" or "l1" => VectorDistanceMetric.Manhattan,
            "chebyshev" => VectorDistanceMetric.Chebyshev,
            "hamming" => VectorDistanceMetric.Hamming,
            "mahalanobis" => VectorDistanceMetric.Mahalanobis,
            _ => Enum.TryParse<VectorDistanceMetric>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this PromptStrategyPattern val) => val switch
    {
        PromptStrategyPattern.ZeroShot => "zero_shot",
        PromptStrategyPattern.FewShot => "few_shot",
        PromptStrategyPattern.ChainOfThought => "chain_of_thought",
        PromptStrategyPattern.TreeOfThoughts => "tree_of_thoughts",
        PromptStrategyPattern.ReAct => "react",
        PromptStrategyPattern.SelfConsistency => "self_consistency",
        PromptStrategyPattern.DirectionalStimulus => "directional_stimulus",
        PromptStrategyPattern.SkeletonOfThought => "skeleton_of_thought",
        PromptStrategyPattern.SystemPromptOnly => "system_prompt_only",
        _ => "zero_shot"
    };

    public static PromptStrategyPattern ParsePromptStrategyPattern(string? s, PromptStrategyPattern defaultValue = PromptStrategyPattern.ZeroShot)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "zero_shot" or "zeroshot" => PromptStrategyPattern.ZeroShot,
            "few_shot" or "fewshot" => PromptStrategyPattern.FewShot,
            "chain_of_thought" or "cot" => PromptStrategyPattern.ChainOfThought,
            "tree_of_thoughts" or "tot" => PromptStrategyPattern.TreeOfThoughts,
            "react" => PromptStrategyPattern.ReAct,
            "self_consistency" => PromptStrategyPattern.SelfConsistency,
            "directional_stimulus" => PromptStrategyPattern.DirectionalStimulus,
            "skeleton_of_thought" => PromptStrategyPattern.SkeletonOfThought,
            "system_prompt_only" => PromptStrategyPattern.SystemPromptOnly,
            _ => Enum.TryParse<PromptStrategyPattern>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this QuantizationPrecision val) => val switch
    {
        QuantizationPrecision.Float32 => "fp32",
        QuantizationPrecision.Float16 => "fp16",
        QuantizationPrecision.BFloat16 => "bf16",
        QuantizationPrecision.Int8 => "int8",
        QuantizationPrecision.Int4 => "int4",
        QuantizationPrecision.Gptq4Bit => "gptq_4bit",
        QuantizationPrecision.Awq4Bit => "awq_4bit",
        QuantizationPrecision.GgmlQ4_0 => "ggml_q4_0",
        QuantizationPrecision.GgmlQ8_0 => "ggml_q8_0",
        QuantizationPrecision.FP8 => "fp8",
        _ => "fp16"
    };

    public static QuantizationPrecision ParseQuantizationPrecision(string? s, QuantizationPrecision defaultValue = QuantizationPrecision.Float16)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "fp32" or "float32" => QuantizationPrecision.Float32,
            "fp16" or "float16" => QuantizationPrecision.Float16,
            "bf16" or "bfloat16" => QuantizationPrecision.BFloat16,
            "int8" => QuantizationPrecision.Int8,
            "int4" => QuantizationPrecision.Int4,
            "gptq_4bit" or "gptq" => QuantizationPrecision.Gptq4Bit,
            "awq_4bit" or "awq" => QuantizationPrecision.Awq4Bit,
            "ggml_q4_0" or "q4_0" => QuantizationPrecision.GgmlQ4_0,
            "ggml_q8_0" or "q8_0" => QuantizationPrecision.GgmlQ8_0,
            "fp8" or "float8" => QuantizationPrecision.FP8,
            _ => Enum.TryParse<QuantizationPrecision>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this FineTuningMethodKind val) => val switch
    {
        FineTuningMethodKind.FullFineTuning => "full_fine_tuning",
        FineTuningMethodKind.LoRA => "lora",
        FineTuningMethodKind.QLoRA => "qlora",
        FineTuningMethodKind.PrefixTuning => "prefix_tuning",
        FineTuningMethodKind.PromptTuning => "prompt_tuning",
        FineTuningMethodKind.AdapterLayers => "adapter_layers",
        FineTuningMethodKind.DPO => "dpo",
        FineTuningMethodKind.PPO => "ppo",
        FineTuningMethodKind.RLHF => "rlhf",
        _ => "lora"
    };

    public static FineTuningMethodKind ParseFineTuningMethodKind(string? s, FineTuningMethodKind defaultValue = FineTuningMethodKind.LoRA)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "full_fine_tuning" or "full" => FineTuningMethodKind.FullFineTuning,
            "lora" => FineTuningMethodKind.LoRA,
            "qlora" => FineTuningMethodKind.QLoRA,
            "prefix_tuning" or "prefix" => FineTuningMethodKind.PrefixTuning,
            "prompt_tuning" => FineTuningMethodKind.PromptTuning,
            "adapter_layers" or "adapters" => FineTuningMethodKind.AdapterLayers,
            "dpo" => FineTuningMethodKind.DPO,
            "ppo" => FineTuningMethodKind.PPO,
            "rlhf" => FineTuningMethodKind.RLHF,
            _ => Enum.TryParse<FineTuningMethodKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this DatasetSplitKind val) => val switch
    {
        DatasetSplitKind.Train => "train",
        DatasetSplitKind.Validation => "validation",
        DatasetSplitKind.Test => "test",
        DatasetSplitKind.Holdout => "holdout",
        DatasetSplitKind.CrossValidationFold => "cv_fold",
        DatasetSplitKind.Calibration => "calibration",
        _ => "train"
    };

    public static DatasetSplitKind ParseDatasetSplitKind(string? s, DatasetSplitKind defaultValue = DatasetSplitKind.Train)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "train" or "training" => DatasetSplitKind.Train,
            "validation" or "val" or "dev" => DatasetSplitKind.Validation,
            "test" or "testing" => DatasetSplitKind.Test,
            "holdout" => DatasetSplitKind.Holdout,
            "cv_fold" or "fold" => DatasetSplitKind.CrossValidationFold,
            "calibration" => DatasetSplitKind.Calibration,
            _ => Enum.TryParse<DatasetSplitKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this ConfusionMatrixQuadrant val) => val switch
    {
        ConfusionMatrixQuadrant.TruePositive => "true_positive",
        ConfusionMatrixQuadrant.FalsePositive => "false_positive",
        ConfusionMatrixQuadrant.TrueNegative => "true_negative",
        ConfusionMatrixQuadrant.FalseNegative => "false_negative",
        _ => "true_positive"
    };

    public static ConfusionMatrixQuadrant ParseConfusionMatrixQuadrant(string? s, ConfusionMatrixQuadrant defaultValue = ConfusionMatrixQuadrant.TruePositive)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "true_positive" or "tp" => ConfusionMatrixQuadrant.TruePositive,
            "false_positive" or "fp" => ConfusionMatrixQuadrant.FalsePositive,
            "true_negative" or "tn" => ConfusionMatrixQuadrant.TrueNegative,
            "false_negative" or "fn" => ConfusionMatrixQuadrant.FalseNegative,
            _ => Enum.TryParse<ConfusionMatrixQuadrant>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this RoCScoreType val) => val switch
    {
        RoCScoreType.AucRoc => "auc_roc",
        RoCScoreType.PrecisionRecallAuc => "pr_auc",
        RoCScoreType.LogLoss => "log_loss",
        RoCScoreType.GMean => "g_mean",
        RoCScoreType.YoudensJ => "youdens_j",
        RoCScoreType.F2Score => "f2_score",
        RoCScoreType.F0_5Score => "f0_5_score",
        _ => "auc_roc"
    };

    public static RoCScoreType ParseRoCScoreType(string? s, RoCScoreType defaultValue = RoCScoreType.AucRoc)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "auc_roc" or "aucroc" or "roc_auc" => RoCScoreType.AucRoc,
            "pr_auc" or "prauc" => RoCScoreType.PrecisionRecallAuc,
            "log_loss" or "logloss" => RoCScoreType.LogLoss,
            "g_mean" or "gmean" => RoCScoreType.GMean,
            "youdens_j" or "youden" => RoCScoreType.YoudensJ,
            "f2_score" or "f2" => RoCScoreType.F2Score,
            "f0_5_score" or "f0_5" or "f05" => RoCScoreType.F0_5Score,
            _ => Enum.TryParse<RoCScoreType>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this SyntheticDataGenerator val) => val switch
    {
        SyntheticDataGenerator.LlmAugmentation => "llm_augmentation",
        SyntheticDataGenerator.GAN => "gan",
        SyntheticDataGenerator.VariationalAutoencoder => "vae",
        SyntheticDataGenerator.RuleBasedMutation => "rule_based_mutation",
        SyntheticDataGenerator.BackTranslation => "back_translation",
        SyntheticDataGenerator.OversamplingSMOTE => "smote",
        _ => "llm_augmentation"
    };

    public static SyntheticDataGenerator ParseSyntheticDataGenerator(string? s, SyntheticDataGenerator defaultValue = SyntheticDataGenerator.LlmAugmentation)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "llm_augmentation" or "llm" => SyntheticDataGenerator.LlmAugmentation,
            "gan" => SyntheticDataGenerator.GAN,
            "vae" or "variational_autoencoder" => SyntheticDataGenerator.VariationalAutoencoder,
            "rule_based_mutation" or "rules" => SyntheticDataGenerator.RuleBasedMutation,
            "back_translation" => SyntheticDataGenerator.BackTranslation,
            "smote" or "oversampling_smote" => SyntheticDataGenerator.OversamplingSMOTE,
            _ => Enum.TryParse<SyntheticDataGenerator>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this ModelDriftMetric val) => val switch
    {
        ModelDriftMetric.PopulationStabilityIndex => "psi",
        ModelDriftMetric.WassersteinDistance => "wasserstein_distance",
        ModelDriftMetric.KolmogorovSmirnovTest => "ks_test",
        ModelDriftMetric.KullbackLeiblerDivergence => "kl_divergence",
        ModelDriftMetric.JensenShannonDivergence => "js_divergence",
        ModelDriftMetric.ConceptDriftRate => "concept_drift_rate",
        _ => "psi"
    };

    public static ModelDriftMetric ParseModelDriftMetric(string? s, ModelDriftMetric defaultValue = ModelDriftMetric.PopulationStabilityIndex)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "psi" or "population_stability_index" => ModelDriftMetric.PopulationStabilityIndex,
            "wasserstein_distance" or "wasserstein" or "earth_movers" => ModelDriftMetric.WassersteinDistance,
            "ks_test" or "kolmogorov_smirnov" => ModelDriftMetric.KolmogorovSmirnovTest,
            "kl_divergence" or "kullback_leibler" => ModelDriftMetric.KullbackLeiblerDivergence,
            "js_divergence" or "jensen_shannon" => ModelDriftMetric.JensenShannonDivergence,
            "concept_drift_rate" or "concept_drift" => ModelDriftMetric.ConceptDriftRate,
            _ => Enum.TryParse<ModelDriftMetric>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this FeatureImportanceKind val) => val switch
    {
        FeatureImportanceKind.ShapleyValues => "shapley_values",
        FeatureImportanceKind.IntegratedGradients => "integrated_gradients",
        FeatureImportanceKind.PermutationImportance => "permutation_importance",
        FeatureImportanceKind.GiniImpurity => "gini_impurity",
        FeatureImportanceKind.Gain => "gain",
        FeatureImportanceKind.AttentionWeights => "attention_weights",
        _ => "shapley_values"
    };

    public static FeatureImportanceKind ParseFeatureImportanceKind(string? s, FeatureImportanceKind defaultValue = FeatureImportanceKind.ShapleyValues)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "shapley_values" or "shap" => FeatureImportanceKind.ShapleyValues,
            "integrated_gradients" or "ig" => FeatureImportanceKind.IntegratedGradients,
            "permutation_importance" or "permutation" => FeatureImportanceKind.PermutationImportance,
            "gini_impurity" or "gini" => FeatureImportanceKind.GiniImpurity,
            "gain" => FeatureImportanceKind.Gain,
            "attention_weights" or "attention" => FeatureImportanceKind.AttentionWeights,
            _ => Enum.TryParse<FeatureImportanceKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this ModelRegistryStatus val) => val switch
    {
        ModelRegistryStatus.Draft => "draft",
        ModelRegistryStatus.InReview => "in_review",
        ModelRegistryStatus.Approved => "approved",
        ModelRegistryStatus.Staging => "staging",
        ModelRegistryStatus.Production => "production",
        ModelRegistryStatus.Deprecated => "deprecated",
        ModelRegistryStatus.Archived => "archived",
        ModelRegistryStatus.Rejected => "rejected",
        _ => "draft"
    };

    public static ModelRegistryStatus ParseModelRegistryStatus(string? s, ModelRegistryStatus defaultValue = ModelRegistryStatus.Draft)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "draft" => ModelRegistryStatus.Draft,
            "in_review" or "review" => ModelRegistryStatus.InReview,
            "approved" => ModelRegistryStatus.Approved,
            "staging" => ModelRegistryStatus.Staging,
            "production" or "prod" => ModelRegistryStatus.Production,
            "deprecated" => ModelRegistryStatus.Deprecated,
            "archived" => ModelRegistryStatus.Archived,
            "rejected" => ModelRegistryStatus.Rejected,
            _ => Enum.TryParse<ModelRegistryStatus>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this HyperparameterSearchStrategy val) => val switch
    {
        HyperparameterSearchStrategy.GridSearch => "grid_search",
        HyperparameterSearchStrategy.RandomSearch => "random_search",
        HyperparameterSearchStrategy.BayesianOptimization => "bayesian_optimization",
        HyperparameterSearchStrategy.Hyperband => "hyperband",
        HyperparameterSearchStrategy.TreeStructuredParzen => "tpe",
        HyperparameterSearchStrategy.EvolutionaryAlgorithm => "evolutionary",
        _ => "grid_search"
    };

    public static HyperparameterSearchStrategy ParseHyperparameterSearchStrategy(string? s, HyperparameterSearchStrategy defaultValue = HyperparameterSearchStrategy.GridSearch)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "grid_search" or "grid" => HyperparameterSearchStrategy.GridSearch,
            "random_search" or "random" => HyperparameterSearchStrategy.RandomSearch,
            "bayesian_optimization" or "bayesian" or "bayes" => HyperparameterSearchStrategy.BayesianOptimization,
            "hyperband" => HyperparameterSearchStrategy.Hyperband,
            "tpe" or "tree_structured_parzen" => HyperparameterSearchStrategy.TreeStructuredParzen,
            "evolutionary" or "genetic" => HyperparameterSearchStrategy.EvolutionaryAlgorithm,
            _ => Enum.TryParse<HyperparameterSearchStrategy>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this InferenceLatencyPercentile val) => val switch
    {
        InferenceLatencyPercentile.P50 => "p50",
        InferenceLatencyPercentile.P75 => "p75",
        InferenceLatencyPercentile.P90 => "p90",
        InferenceLatencyPercentile.P95 => "p95",
        InferenceLatencyPercentile.P99 => "p99",
        InferenceLatencyPercentile.P99_9 => "p99_9",
        InferenceLatencyPercentile.Mean => "mean",
        InferenceLatencyPercentile.Max => "max",
        _ => "p50"
    };

    public static InferenceLatencyPercentile ParseInferenceLatencyPercentile(string? s, InferenceLatencyPercentile defaultValue = InferenceLatencyPercentile.P50)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "p50" or "p_50" or "median" => InferenceLatencyPercentile.P50,
            "p75" or "p_75" => InferenceLatencyPercentile.P75,
            "p90" or "p_90" => InferenceLatencyPercentile.P90,
            "p95" or "p_95" => InferenceLatencyPercentile.P95,
            "p99" or "p_99" => InferenceLatencyPercentile.P99,
            "p99_9" or "p999" or "p99.9" => InferenceLatencyPercentile.P99_9,
            "mean" or "avg" or "average" => InferenceLatencyPercentile.Mean,
            "max" or "maximum" => InferenceLatencyPercentile.Max,
            _ => Enum.TryParse<InferenceLatencyPercentile>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this AiModelGateType val) => val switch
    {
        AiModelGateType.SafetyFilter => "safety_filter",
        AiModelGateType.QualityThreshold => "quality_threshold",
        AiModelGateType.CostBudget => "cost_budget",
        AiModelGateType.LatencySla => "latency_sla",
        AiModelGateType.ContentModeration => "content_moderation",
        AiModelGateType.HallucinationDetector => "hallucination_detector",
        _ => "safety_filter"
    };

    public static AiModelGateType ParseAiModelGateType(string? s, AiModelGateType defaultValue = AiModelGateType.SafetyFilter)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "safety_filter" or "safety" => AiModelGateType.SafetyFilter,
            "quality_threshold" or "quality" => AiModelGateType.QualityThreshold,
            "cost_budget" or "cost" => AiModelGateType.CostBudget,
            "latency_sla" or "latency" => AiModelGateType.LatencySla,
            "content_moderation" or "moderation" => AiModelGateType.ContentModeration,
            "hallucination_detector" or "hallucination" => AiModelGateType.HallucinationDetector,
            _ => Enum.TryParse<AiModelGateType>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this CloudProviderKind val) => val switch
    {
        CloudProviderKind.Aws => "aws",
        CloudProviderKind.Azure => "azure",
        CloudProviderKind.Gcp => "gcp",
        CloudProviderKind.Cloudflare => "cloudflare",
        CloudProviderKind.DigitalOcean => "digital_ocean",
        CloudProviderKind.OracleCloud => "oracle_cloud",
        CloudProviderKind.OnPremise => "on_premise",
        CloudProviderKind.Hybrid => "hybrid",
        _ => "aws"
    };

    public static CloudProviderKind ParseCloudProviderKind(string? s, CloudProviderKind defaultValue = CloudProviderKind.Aws)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "aws" or "amazon" => CloudProviderKind.Aws,
            "azure" or "microsoft" => CloudProviderKind.Azure,
            "gcp" or "google" => CloudProviderKind.Gcp,
            "cloudflare" => CloudProviderKind.Cloudflare,
            "digital_ocean" or "digitalocean" => CloudProviderKind.DigitalOcean,
            "oracle_cloud" or "oracle" or "oci" => CloudProviderKind.OracleCloud,
            "on_premise" or "onprem" => CloudProviderKind.OnPremise,
            "hybrid" => CloudProviderKind.Hybrid,
            _ => Enum.TryParse<CloudProviderKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this ContainerEngineKind val) => val switch
    {
        ContainerEngineKind.Docker => "docker",
        ContainerEngineKind.Podman => "podman",
        ContainerEngineKind.Containerd => "containerd",
        ContainerEngineKind.CriO => "cri_o",
        ContainerEngineKind.Singularity => "singularity",
        _ => "docker"
    };

    public static ContainerEngineKind ParseContainerEngineKind(string? s, ContainerEngineKind defaultValue = ContainerEngineKind.Docker)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "docker" => ContainerEngineKind.Docker,
            "podman" => ContainerEngineKind.Podman,
            "containerd" => ContainerEngineKind.Containerd,
            "cri_o" or "crio" => ContainerEngineKind.CriO,
            "singularity" => ContainerEngineKind.Singularity,
            _ => Enum.TryParse<ContainerEngineKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this OrchestratorKind val) => val switch
    {
        OrchestratorKind.Kubernetes => "kubernetes",
        OrchestratorKind.DockerSwarm => "docker_swarm",
        OrchestratorKind.Nomad => "nomad",
        OrchestratorKind.AmazonEcs => "amazon_ecs",
        OrchestratorKind.AzureAks => "azure_aks",
        OrchestratorKind.GoogleGke => "google_gke",
        _ => "kubernetes"
    };

    public static OrchestratorKind ParseOrchestratorKind(string? s, OrchestratorKind defaultValue = OrchestratorKind.Kubernetes)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "kubernetes" or "k8s" => OrchestratorKind.Kubernetes,
            "docker_swarm" or "swarm" => OrchestratorKind.DockerSwarm,
            "nomad" => OrchestratorKind.Nomad,
            "amazon_ecs" or "ecs" => OrchestratorKind.AmazonEcs,
            "azure_aks" or "aks" => OrchestratorKind.AzureAks,
            "google_gke" or "gke" => OrchestratorKind.GoogleGke,
            _ => Enum.TryParse<OrchestratorKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this DeploymentStrategyKind val) => val switch
    {
        DeploymentStrategyKind.RollingUpdate => "rolling_update",
        DeploymentStrategyKind.BlueGreen => "blue_green",
        DeploymentStrategyKind.Canary => "canary",
        DeploymentStrategyKind.Recreate => "recreate",
        DeploymentStrategyKind.ATesting => "a_b_testing",
        DeploymentStrategyKind.Shadow => "shadow",
        _ => "rolling_update"
    };

    public static DeploymentStrategyKind ParseDeploymentStrategyKind(string? s, DeploymentStrategyKind defaultValue = DeploymentStrategyKind.RollingUpdate)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "rolling_update" or "rolling" => DeploymentStrategyKind.RollingUpdate,
            "blue_green" or "bluegreen" => DeploymentStrategyKind.BlueGreen,
            "canary" => DeploymentStrategyKind.Canary,
            "recreate" => DeploymentStrategyKind.Recreate,
            "a_b_testing" or "ab_testing" or "ab" => DeploymentStrategyKind.ATesting,
            "shadow" => DeploymentStrategyKind.Shadow,
            _ => Enum.TryParse<DeploymentStrategyKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this ServiceMeshProtocol val) => val switch
    {
        ServiceMeshProtocol.Istio => "istio",
        ServiceMeshProtocol.Linkerd => "linkerd",
        ServiceMeshProtocol.Consul => "consul",
        ServiceMeshProtocol.Envoy => "envoy",
        ServiceMeshProtocol.Traefik => "traefik",
        ServiceMeshProtocol.OpenTelemetry => "opentelemetry",
        _ => "istio"
    };

    public static ServiceMeshProtocol ParseServiceMeshProtocol(string? s, ServiceMeshProtocol defaultValue = ServiceMeshProtocol.Istio)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "istio" => ServiceMeshProtocol.Istio,
            "linkerd" => ServiceMeshProtocol.Linkerd,
            "consul" => ServiceMeshProtocol.Consul,
            "envoy" => ServiceMeshProtocol.Envoy,
            "traefik" => ServiceMeshProtocol.Traefik,
            "opentelemetry" or "otel" => ServiceMeshProtocol.OpenTelemetry,
            _ => Enum.TryParse<ServiceMeshProtocol>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this LoadBalancerAlgorithm val) => val switch
    {
        LoadBalancerAlgorithm.RoundRobin => "round_robin",
        LoadBalancerAlgorithm.LeastConnections => "least_connections",
        LoadBalancerAlgorithm.IPHash => "ip_hash",
        LoadBalancerAlgorithm.WeightedRoundRobin => "weighted_round_robin",
        LoadBalancerAlgorithm.RandomChoice => "random_choice",
        LoadBalancerAlgorithm.LeastResponseTime => "least_response_time",
        _ => "round_robin"
    };

    public static LoadBalancerAlgorithm ParseLoadBalancerAlgorithm(string? s, LoadBalancerAlgorithm defaultValue = LoadBalancerAlgorithm.RoundRobin)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "round_robin" => LoadBalancerAlgorithm.RoundRobin,
            "least_connections" or "least_conn" => LoadBalancerAlgorithm.LeastConnections,
            "ip_hash" => LoadBalancerAlgorithm.IPHash,
            "weighted_round_robin" => LoadBalancerAlgorithm.WeightedRoundRobin,
            "random_choice" or "random" => LoadBalancerAlgorithm.RandomChoice,
            "least_response_time" or "least_time" => LoadBalancerAlgorithm.LeastResponseTime,
            _ => Enum.TryParse<LoadBalancerAlgorithm>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this AutoScalingMetricKind val) => val switch
    {
        AutoScalingMetricKind.CpuUtilization => "cpu_utilization",
        AutoScalingMetricKind.MemoryUtilization => "memory_utilization",
        AutoScalingMetricKind.RequestRate => "request_rate",
        AutoScalingMetricKind.QueueLength => "queue_length",
        AutoScalingMetricKind.CustomMetric => "custom_metric",
        AutoScalingMetricKind.CpuAndMemoryCombined => "cpu_and_memory_combined",
        _ => "cpu_utilization"
    };

    public static AutoScalingMetricKind ParseAutoScalingMetricKind(string? s, AutoScalingMetricKind defaultValue = AutoScalingMetricKind.CpuUtilization)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "cpu_utilization" or "cpu" => AutoScalingMetricKind.CpuUtilization,
            "memory_utilization" or "memory" or "ram" => AutoScalingMetricKind.MemoryUtilization,
            "request_rate" or "rps" => AutoScalingMetricKind.RequestRate,
            "queue_length" or "queue" => AutoScalingMetricKind.QueueLength,
            "custom_metric" or "custom" => AutoScalingMetricKind.CustomMetric,
            "cpu_and_memory_combined" or "combined" => AutoScalingMetricKind.CpuAndMemoryCombined,
            _ => Enum.TryParse<AutoScalingMetricKind>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this ClusterNodeRole val) => val switch
    {
        ClusterNodeRole.ControlPlane => "control_plane",
        ClusterNodeRole.Worker => "worker",
        ClusterNodeRole.IngressGateway => "ingress_gateway",
        ClusterNodeRole.StorageNode => "storage_node",
        ClusterNodeRole.AiAccelerator => "ai_accelerator",
        ClusterNodeRole.EdgeNode => "edge_node",
        _ => "worker"
    };

    public static ClusterNodeRole ParseClusterNodeRole(string? s, ClusterNodeRole defaultValue = ClusterNodeRole.Worker)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "control_plane" or "master" => ClusterNodeRole.ControlPlane,
            "worker" or "node" => ClusterNodeRole.Worker,
            "ingress_gateway" or "ingress" => ClusterNodeRole.IngressGateway,
            "storage_node" or "storage" => ClusterNodeRole.StorageNode,
            "ai_accelerator" or "gpu" or "tpu" => ClusterNodeRole.AiAccelerator,
            "edge_node" or "edge" => ClusterNodeRole.EdgeNode,
            _ => Enum.TryParse<ClusterNodeRole>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this SecretStorageBackend val) => val switch
    {
        SecretStorageBackend.HashiCorpVault => "hashicorp_vault",
        SecretStorageBackend.AwsSecretsManager => "aws_secrets_manager",
        SecretStorageBackend.AzureKeyVault => "azure_key_vault",
        SecretStorageBackend.GoogleSecretManager => "google_secret_manager",
        SecretStorageBackend.KubernetesSecret => "kubernetes_secret",
        SecretStorageBackend.EnvironmentVariable => "environment_variable",
        _ => "environment_variable"
    };

    public static SecretStorageBackend ParseSecretStorageBackend(string? s, SecretStorageBackend defaultValue = SecretStorageBackend.EnvironmentVariable)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "hashicorp_vault" or "vault" => SecretStorageBackend.HashiCorpVault,
            "aws_secrets_manager" or "aws_secrets" => SecretStorageBackend.AwsSecretsManager,
            "azure_key_vault" or "key_vault" => SecretStorageBackend.AzureKeyVault,
            "google_secret_manager" or "gsm" => SecretStorageBackend.GoogleSecretManager,
            "kubernetes_secret" or "k8s_secret" => SecretStorageBackend.KubernetesSecret,
            "environment_variable" or "env" => SecretStorageBackend.EnvironmentVariable,
            _ => Enum.TryParse<SecretStorageBackend>(s, true, out var r) ? r : defaultValue
        };
    }

    public static string ToApiString(this CdnCacheStrategy val) => val switch
    {
        CdnCacheStrategy.CacheEverything => "cache_everything",
        CdnCacheStrategy.BypassCache => "bypass_cache",
        CdnCacheStrategy.CacheStaticOnly => "cache_static_only",
        CdnCacheStrategy.StaleWhileRevalidate => "stale_while_revalidate",
        CdnCacheStrategy.CacheByQueryString => "cache_by_query_string",
        CdnCacheStrategy.EdgeSideIncludes => "edge_side_includes",
        _ => "cache_everything"
    };

    public static CdnCacheStrategy ParseCdnCacheStrategy(string? s, CdnCacheStrategy defaultValue = CdnCacheStrategy.CacheEverything)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var clean = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return clean switch
        {
            "cache_everything" or "all" => CdnCacheStrategy.CacheEverything,
            "bypass_cache" or "bypass" or "no_cache" => CdnCacheStrategy.BypassCache,
            "cache_static_only" or "static" => CdnCacheStrategy.CacheStaticOnly,
            "stale_while_revalidate" or "swr" => CdnCacheStrategy.StaleWhileRevalidate,
            "cache_by_query_string" or "query_string" => CdnCacheStrategy.CacheByQueryString,
            "edge_side_includes" or "esi" => CdnCacheStrategy.EdgeSideIncludes,
            _ => Enum.TryParse<CdnCacheStrategy>(s, true, out var r) ? r : defaultValue
        };
    }
}

#endregion
