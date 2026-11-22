# Keep Python 3.12 while using Ubuntu LTS security backports for the native image dependencies.
FROM ubuntu:24.04

# Set environment variables
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PYTHONPATH=/app \
    OMP_NUM_THREADS=1 \
    OPENBLAS_NUM_THREADS=1 \
    MKL_NUM_THREADS=1

ENV PATH="/opt/venv/bin:$PATH"

WORKDIR /app

# Install system dependencies (curl for container healthcheck)
RUN apt-get update && apt-get upgrade -y -o Acquire::Retries=0 \
    && apt-get install -y --no-install-recommends \
    python3.12 python3.12-venv curl libgomp1 \
    && rm -rf /var/lib/apt/lists/*
RUN python3.12 -m venv /opt/venv

# Copy and install python dependencies
COPY requirements.txt requirements.lock ./
RUN pip install --no-cache-dir \
    -c requirements.lock \
    -r requirements.txt

ARG INSTALL_RESEARCH_CNN=false
COPY requirements-cnn.txt requirements-avr.txt ./
RUN if [ "$INSTALL_RESEARCH_CNN" = "true" ]; then \
    pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu -r requirements-cnn.txt; \
    pip install --no-cache-dir -r requirements-avr.txt; \
    fi

# PyTorch requires setuptools; update its vulnerable vendored dependencies too.
RUN pip install --no-cache-dir -c requirements.lock setuptools && pip check

# Create non-root user for container security
RUN useradd -m -u 1001 appuser

# Copy application source code and configuration
COPY ./app /app/app
COPY ./config /app/config
COPY ./scripts /app/scripts

# Set ownership
RUN chown -R appuser:appuser /app
USER appuser

# Expose service port
EXPOSE 8000

# Container healthcheck
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8000/health || exit 1

# A single process keeps metrics and research admission limits consistent; sync inference uses the thread pool.
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1"]
