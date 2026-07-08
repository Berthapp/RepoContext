export type LogLevel = "debug" | "info" | "warn" | "error";

export interface Logger {
  log(level: LogLevel, message: string): void;
}

export class ConsoleLogger implements Logger {
  constructor(private readonly prefix: string = "app") {}

  log(level: LogLevel, message: string): void {
    // eslint-disable-next-line no-console
    console[level === "debug" ? "log" : level](`[${this.prefix}] ${message}`);
  }
}

export const defaultLogger: Logger = new ConsoleLogger();
