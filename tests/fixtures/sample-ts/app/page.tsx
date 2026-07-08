import React from "react";
import { LoginForm } from "../src/components/LoginForm";
import { loginUser } from "../src/auth/login";

export default function HomePage(): React.JSX.Element {
  return <LoginForm onSubmit={(creds) => void loginUser(creds)} />;
}
