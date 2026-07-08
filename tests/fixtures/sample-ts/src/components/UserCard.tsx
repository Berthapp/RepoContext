import React from "react";

export interface UserCardProps {
  email: string;
  isAdmin?: boolean;
}

export function UserCard({ email, isAdmin = false }: UserCardProps): React.JSX.Element {
  return (
    <div className="user-card">
      <span className="email">{email}</span>
      {isAdmin ? <span className="badge">admin</span> : null}
    </div>
  );
}

export default UserCard;
